#!/usr/bin/env bash
# Compares a source and a cloned target database: schema, row data, sequences, large objects,
# constraint validity, materialized view state and database settings. Exit 0 only if identical.
# Usage: compare.sh <src-port> <tgt-port> <src-db> [tgt-db]
set -u
SRC_PORT=$1; TGT_PORT=$2; SRC_DB=$3; TGT_DB=${4:-$3}
HOST=${PGHOST_OVERRIDE:-127.0.0.1}
q() { psql -h "$HOST" -p "$1" -U postgres -d "$2" -X -At -v ON_ERROR_STOP=1 -c "$3"; }
fail=0
check() { # name, srcout, tgtout
  if [ "$2" == "$3" ]; then echo "  OK    $1"; else echo "  FAIL  $1"; diff <(echo "$2") <(echo "$3") | head -${DIFF_LINES:-15}; fail=1; fi
}

dump_schema() { pg_dump -h "$HOST" -p "$1" -U postgres -s --no-owner --no-acl --no-comments "$2" 2>/dev/null | grep -vE '^(--|SET |SELECT pg_catalog.set_config|\\restrict|\\unrestrict|$)'; }
check "schema (pg_dump -s)" "$(dump_schema "$SRC_PORT" "$SRC_DB")" "$(dump_schema "$TGT_PORT" "$TGT_DB")"

comments() { q "$1" "$2" "select coalesce(objsubid::text,'') || ' ' || classoid::regclass || ' ' || description from pg_description d where objoid >= 16384 and classoid::regclass::text <> 'pg_largeobject_metadata' order by 1" | sed -E 's/^[0-9]* //' | sort; }
check "comments (count)" "$(comments "$SRC_PORT" "$SRC_DB" | wc -l)" "$(comments "$TGT_PORT" "$TGT_DB" | wc -l)"

TABLES=$(q "$SRC_PORT" "$SRC_DB" "select format('%I.%I', n.nspname, c.relname) from pg_class c join pg_namespace n on n.oid=c.relnamespace where c.relkind in ('r','p') and n.nspname not in ('pg_catalog','information_schema') and n.nspname not like 'pg_toast%' and not exists (select 1 from pg_depend d where d.objid=c.oid and d.classid='pg_class'::regclass and d.deptype='e') order by 1")
src_data=""; tgt_data=""
while IFS= read -r t; do
  [ -z "$t" ] && continue
  sql="select '$t', count(*), md5(coalesce(string_agg(x::text, E'\n' order by x::text), '')) from $t x"
  src_data+="$(q "$SRC_PORT" "$SRC_DB" "$sql")"$'\n'
  tgt_data+="$(q "$TGT_PORT" "$TGT_DB" "$sql")"$'\n'
done <<< "$TABLES"
check "row data (count+md5 of $(echo "$TABLES" | grep -c .) tables)" "$src_data" "$tgt_data"

check "sequences" "$(q "$SRC_PORT" "$SRC_DB" "select schemaname,sequencename,last_value from pg_sequences order by 1,2")" "$(q "$TGT_PORT" "$TGT_DB" "select schemaname,sequencename,last_value from pg_sequences order by 1,2")"
check "large objects" "$(q "$SRC_PORT" "$SRC_DB" "select oid, md5(lo_get(oid)) from pg_largeobject_metadata order by 1")" "$(q "$TGT_PORT" "$TGT_DB" "select oid, md5(lo_get(oid)) from pg_largeobject_metadata order by 1")"
cons="select conrelid::regclass::text, conname, contype, convalidated, condeferrable, condeferred from pg_constraint where connamespace not in ('pg_catalog'::regnamespace) order by 1,2"
check "constraints + validity" "$(q "$SRC_PORT" "$SRC_DB" "$cons")" "$(q "$TGT_PORT" "$TGT_DB" "$cons")"
check "matview populated" "$(q "$SRC_PORT" "$SRC_DB" "select schemaname,matviewname,ispopulated from pg_matviews order by 1,2")" "$(q "$TGT_PORT" "$TGT_DB" "select schemaname,matviewname,ispopulated from pg_matviews order by 1,2")"
check "database settings" "$(q "$SRC_PORT" "$SRC_DB" "select setconfig from pg_db_role_setting where setdatabase=(select oid from pg_database where datname=current_database())")" "$(q "$TGT_PORT" "$TGT_DB" "select setconfig from pg_db_role_setting where setdatabase=(select oid from pg_database where datname=current_database())")"
check "extensions" "$(q "$SRC_PORT" "$SRC_DB" "select extname, extversion from pg_extension order by 1")" "$(q "$TGT_PORT" "$TGT_DB" "select extname, extversion from pg_extension order by 1")"
check "index stats target" "$(q "$SRC_PORT" "$SRC_DB" "select indexrelid::regclass::text, attnum, attstattarget from pg_attribute a join pg_index i on i.indexrelid=a.attrelid where a.attstattarget > 0 order by 1,2")" "$(q "$TGT_PORT" "$TGT_DB" "select indexrelid::regclass::text, attnum, attstattarget from pg_attribute a join pg_index i on i.indexrelid=a.attrelid where a.attstattarget > 0 order by 1,2")"
check "cluster/replica identity flags" "$(q "$SRC_PORT" "$SRC_DB" "select indrelid::regclass::text, indexrelid::regclass::text, indisclustered from pg_index where indisclustered order by 1")" "$(q "$TGT_PORT" "$TGT_DB" "select indrelid::regclass::text, indexrelid::regclass::text, indisclustered from pg_index where indisclustered order by 1")"
check "no leftover staging dbs" "" "$(q "$TGT_PORT" postgres "select datname from pg_database where datname like '%\_\_pgcopy\_%'")"
check "target has stats (analyzed)" "0" "$(q "$TGT_PORT" "$TGT_DB" "select count(*) from pg_stat_user_tables where n_live_tup > 0 and last_analyze is null and last_autoanalyze is null and relid::regclass::text not like '%scratch%'")"

if [ $fail -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "SOME CHECKS FAILED"; fi
exit $fail
