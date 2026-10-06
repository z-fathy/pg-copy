using System.CommandLine;
using Npgsql;
using PgCopy.Cli;
using PgCopy.Core;
using PgCopy.Core.Catalog;
using PgCopy.Core.Connections;
using PgCopy.Core.Orchestration;
using PgCopy.Core.Snapshots;
using PgCopy.Core.Target;
using Spectre.Console;

var secrets = new FileSecretStore();
var profiles = new ProfileStore(secrets);

ConnectionProfile ResolveProfile(string text) =>
    text.StartsWith("profile:", StringComparison.OrdinalIgnoreCase)
        ? profiles.Load(text["profile:".Length..])
            ?? throw new InvalidOperationException($"No saved profile '{text["profile:".Length..]}'.")
        : ConnectionProfile.Parse(text);

ITargetServer ResolveTarget(string text, string? image, bool safeLoad) =>
    text.Equals("docker", StringComparison.OrdinalIgnoreCase)
        ? new DockerTargetServer(secrets, image, safeLoad)
        : new ExistingTargetServer(ResolveProfile(text).ForDatabase("postgres"));

var sourceOption = new Option<string>("--source", "-s")
{
    Description = "Source connection: postgres:// URI, key=value string, or profile:<name>",
    Required = true,
};
var targetOption = new Option<string>("--target", "-t")
{
    Description = "'docker' (managed container) or a connection to an existing local server",
    DefaultValueFactory = _ => "docker",
};

// ---- databases ----
var databasesCmd = new Command("databases", "List databases on a server with their sizes");
databasesCmd.Options.Add(sourceOption);
databasesCmd.SetAction(
    async (parse, ct) =>
    {
        var profile = ResolveProfile(parse.GetValue(sourceOption)!);
        await using var conn = new NpgsqlConnection(profile.ForDatabase("postgres").ToConnectionString(ConnectionRole.Plain));
        await conn.OpenAsync(ct);
        var table = new Table().AddColumn("Database").AddColumn(new TableColumn("Size").RightAligned());
        foreach (var d in await SourceCatalog.ListDatabasesAsync(conn, ct))
            table.AddRow(Markup.Escape(d.Name), ByteSize.Format(d.SizeBytes));
        AnsiConsole.Write(table);
        return 0;
    }
);

// ---- clone ----
var dbOption = new Option<string[]>("--db", "-d")
{
    Description = "Database to clone (repeatable, or use --all)",
    AllowMultipleArgumentsPerToken = true,
};
var allOption = new Option<bool>("--all") { Description = "Clone every database on the source (except templates/postgres)" };
var asOption = new Option<string?>("--as")
{
    Description = "Local database name (single --db only); defaults to the source name",
};
var jobsOption = new Option<int>("--jobs", "-j") { Description = "Parallel COPY streams", DefaultValueFactory = _ => 8 };
var indexJobsOption = new Option<int>("--index-jobs")
{
    Description = "Parallel index builds (default: half the target CPUs)",
    DefaultValueFactory = _ => 0,
};
var splitOption = new Option<string>("--split-size")
{
    Description = "Split tables larger than this into parallel ranges ('off' to disable)",
    DefaultValueFactory = _ => "1GB",
};
var noBinaryOption = new Option<bool>("--no-binary") { Description = "Always use text COPY" };
var noEarlyOption = new Option<bool>("--no-early-indexes")
{
    Description = "Build all indexes after the copy instead of during it",
};
var validateFksOption = new Option<bool>("--validate-fks")
{
    Description = "Fully validate foreign keys instead of trusting the source",
};
var safeLoadOption = new Option<bool>("--safe-load") { Description = "Keep fsync on during the load (slower)" };
var noSnapshotOption = new Option<bool>("--no-snapshot")
{
    Description = "Skip the consistent snapshot (needed behind transaction poolers; copy may be inconsistent)",
};
var skipMatviewOption = new Option<bool>("--skip-matview-refresh")
{
    Description = "Create materialized views without populating them",
};
var excludeOption = new Option<string[]>("--exclude-table-data")
{
    Description = "Skip rows of tables matching this glob (schema.table)",
    AllowMultipleArgumentsPerToken = true,
};
var ignoreErrorsOption = new Option<bool>("--ignore-restore-errors") { Description = "Treat pg_restore errors as warnings" };
var pgBinOption = new Option<string?>("--pg-bin") { Description = "Directory containing pg_dump/pg_restore" };
var imageOption = new Option<string?>("--image")
{
    Description = "Docker image for the managed target (e.g. postgis/postgis:16-3.4)",
};

var cloneCmd = new Command("clone", "Replicate remote databases to the local machine");
foreach (
    var o in new Option[]
    {
        sourceOption,
        targetOption,
        dbOption,
        allOption,
        asOption,
        jobsOption,
        indexJobsOption,
        splitOption,
        noBinaryOption,
        noEarlyOption,
        validateFksOption,
        safeLoadOption,
        noSnapshotOption,
        skipMatviewOption,
        excludeOption,
        ignoreErrorsOption,
        pgBinOption,
        imageOption,
    }
)
    cloneCmd.Options.Add(o);
cloneCmd.SetAction(
    async (parse, ct) =>
    {
        var source = ResolveProfile(parse.GetValue(sourceOption)!);
        var names = (parse.GetValue(dbOption) ?? []).ToList();
        if (parse.GetValue(allOption))
        {
            await using var conn = new NpgsqlConnection(
                source.ForDatabase("postgres").ToConnectionString(ConnectionRole.Plain)
            );
            await conn.OpenAsync(ct);
            names = (await SourceCatalog.ListDatabasesAsync(conn, ct)).Select(d => d.Name).Where(n => n != "postgres").ToList();
        }
        if (names.Count == 0 && !string.IsNullOrEmpty(source.Database) && source.Database != "postgres")
            names.Add(source.Database);
        if (names.Count == 0)
            throw new InvalidOperationException("Specify --db <name> (repeatable) or --all.");
        var alias = parse.GetValue(asOption);
        if (alias is not null && names.Count != 1)
            throw new InvalidOperationException("--as can only be used with a single database.");

        var options = new CloneOptions
        {
            Jobs = parse.GetValue(jobsOption),
            IndexJobs = parse.GetValue(indexJobsOption),
            SplitSizeBytes = ByteSize.Parse(parse.GetValue(splitOption)!),
            UseBinary = !parse.GetValue(noBinaryOption),
            EarlyIndexes = !parse.GetValue(noEarlyOption),
            TrustedForeignKeys = !parse.GetValue(validateFksOption),
            SafeLoad = parse.GetValue(safeLoadOption),
            UseSnapshot = !parse.GetValue(noSnapshotOption),
            SkipMatviewRefresh = parse.GetValue(skipMatviewOption),
            ExcludeTableData = parse.GetValue(excludeOption) ?? [],
            IgnoreRestoreErrors = parse.GetValue(ignoreErrorsOption),
            PgBinDirectory = parse.GetValue(pgBinOption),
        };

        var target = ResolveTarget(parse.GetValue(targetOption)!, parse.GetValue(imageOption), options.SafeLoad);
        var exit = 0;
        foreach (var name in names)
        {
            var request = new CloneRequest(source, name, alias ?? name, target, options);
            exit |= await ConsoleRunner.RunAsync(request, ct);
        }
        return exit;
    }
);

// ---- docker ----
var dockerCmd = new Command("docker", "Manage the managed target container");
var majorArg = new Argument<int>("major") { Description = "PostgreSQL major version (e.g. 16)" };
var dockerStatus = new Command("status", "Show container status") { majorArg };
dockerStatus.SetAction(
    async (parse, ct) =>
    {
        Console.WriteLine(await DockerTargetServer.StatusAsync(parse.GetValue(majorArg), ct));
        return 0;
    }
);
var dockerStop = new Command("stop", "Stop the container") { majorArg };
dockerStop.SetAction(
    async (parse, ct) =>
    {
        await new DockerTargetServer(secrets).StopAsync(parse.GetValue(majorArg), ct);
        return 0;
    }
);
dockerCmd.Subcommands.Add(dockerStatus);
dockerCmd.Subcommands.Add(dockerStop);

// ---- snapshot ----
var snapshotCmd = new Command("snapshot", "Save/restore instant local save points of a cloned database");
var snapTarget = new Option<string>("--target", "-t")
{
    Description = "Server holding the database (connection string); managed Docker needs --major",
    Required = true,
};
var snapDb = new Argument<string>("database");
var snapName = new Argument<string>("name");
foreach (
    var (verb, desc) in new[]
    {
        ("save", "Save a snapshot"),
        ("restore", "Reset the database to a snapshot"),
        ("delete", "Delete a snapshot"),
    }
)
{
    var cmd = new Command(verb, desc) { snapDb, snapName };
    cmd.Options.Add(snapTarget);
    var v = verb;
    cmd.SetAction(
        async (parse, ct) =>
        {
            var svc = new LocalSnapshotService(ResolveProfile(parse.GetValue(snapTarget)!).ForDatabase("postgres"));
            var (db, name) = (parse.GetValue(snapDb)!, parse.GetValue(snapName)!);
            if (v == "save")
                await svc.SaveAsync(db, name, ct);
            else if (v == "restore")
                await svc.RestoreAsync(db, name, ct);
            else
                await svc.DeleteAsync(db, name, ct);
            AnsiConsole.MarkupLine($"[green]{v}d[/] snapshot [bold]{Markup.Escape(name)}[/] of {Markup.Escape(db)}.");
            return 0;
        }
    );
    snapshotCmd.Subcommands.Add(cmd);
}
var snapList = new Command("list", "List snapshots") { };
snapList.Options.Add(snapTarget);
snapList.SetAction(
    async (parse, ct) =>
    {
        var svc = new LocalSnapshotService(ResolveProfile(parse.GetValue(snapTarget)!).ForDatabase("postgres"));
        var table = new Table().AddColumn("Database").AddColumn("Snapshot").AddColumn(new TableColumn("Size").RightAligned());
        foreach (var s in await svc.ListAsync(null, ct))
            table.AddRow(Markup.Escape(s.Database), Markup.Escape(s.Name), ByteSize.Format(s.SizeBytes));
        AnsiConsole.Write(table);
        return 0;
    }
);
snapshotCmd.Subcommands.Add(snapList);

// ---- profiles ----
var profilesCmd = new Command("profiles", "Manage saved source connections");
var profName = new Argument<string>("name");
var profConn = new Option<string>("--connection", "-c") { Description = "Connection URI or key=value string", Required = true };
var profAdd = new Command("add", "Save (or replace) a profile") { profName };
profAdd.Options.Add(profConn);
profAdd.SetAction(parse =>
{
    profiles.Save(parse.GetValue(profName)!, ConnectionProfile.Parse(parse.GetValue(profConn)!));
    AnsiConsole.MarkupLine("[green]Saved.[/] Use it with --source profile:" + Markup.Escape(parse.GetValue(profName)!));
    return 0;
});
var profRemove = new Command("remove", "Delete a profile") { profName };
profRemove.SetAction(parse => profiles.Remove(parse.GetValue(profName)!) ? 0 : 1);
var profList = new Command("list", "List profiles");
profList.SetAction(_ =>
{
    var table = new Table().AddColumn("Name").AddColumn("Endpoint").AddColumn("Database");
    foreach (var p in profiles.List())
        table.AddRow(Markup.Escape(p.Name), Markup.Escape($"{p.Username}@{p.Host}:{p.Port}"), Markup.Escape(p.Database));
    AnsiConsole.Write(table);
});
profilesCmd.Subcommands.Add(profAdd);
profilesCmd.Subcommands.Add(profRemove);
profilesCmd.Subcommands.Add(profList);

var root = new RootCommand("pg-copy: fast PostgreSQL database replication for development");
foreach (var c in new Command[] { databasesCmd, cloneCmd, dockerCmd, snapshotCmd, profilesCmd })
    root.Subcommands.Add(c);

try
{
    return await root.Parse(args).InvokeAsync();
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException or NpgsqlException or FileNotFoundException)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
    return 1;
}
