namespace PgCopy.IntegrationTests;

/// <summary>A fact that is skipped unless PGCOPY_IT_SOURCE and PGCOPY_IT_TARGET are set.</summary>
public sealed class ItFactAttribute : FactAttribute
{
    public ItFactAttribute()
    {
        if (!ServerFixture.Configured)
            Skip = "Set PGCOPY_IT_SOURCE and PGCOPY_IT_TARGET to run integration tests.";
    }
}

public sealed class ItTheoryAttribute : TheoryAttribute
{
    public ItTheoryAttribute()
    {
        if (!ServerFixture.Configured)
            Skip = "Set PGCOPY_IT_SOURCE and PGCOPY_IT_TARGET to run integration tests.";
    }
}
