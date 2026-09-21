using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// Topic collision detection. Every positive case here is a real collision from the live run on
/// 2026-09-21 that the prompt's exclusion list failed to prevent — this is the mechanical backstop
/// for the ones the model ignores.
/// </summary>
public class TopicKeyTests
{
    private static void Collide(string a, string b) =>
        Assert.True(TopicKey.Collides(a, b), $"expected a collision:\n  \"{a}\" -> [{TopicKey.Of(a)}]\n  \"{b}\" -> [{TopicKey.Of(b)}]");

    private static void Distinct(string a, string b) =>
        Assert.False(TopicKey.Collides(a, b), $"expected NO collision:\n  \"{a}\" -> [{TopicKey.Of(a)}]\n  \"{b}\" -> [{TopicKey.Of(b)}]");

    [Theory]
    // The four pairs the live run produced, in the order they were reported.
    [InlineData("ORM vs raw SQL", "Raw SQL vs ORM")]                                       // pure reordering
    [InlineData("SQL query optimization", "SQL query optimization techniques")]            // filler noun
    [InlineData("API rate limiting", "Rate limiting implementation")]                      // subset + filler
    [InlineData("Caching strategies for database queries", "Caching strategies")]          // subset
    public void The_collisions_the_live_run_produced_are_caught(string a, string b) => Collide(a, b);

    [Theory]
    [InlineData("Stored procedures vs application logic", "Stored procedures vs application logic")]  // verbatim repeat
    [InlineData("Choosing between REST and GraphQL", "REST vs GraphQL")]
    [InlineData("Synchronous vs asynchronous API calls", "Asynchronous vs synchronous API calls")]
    public void More_of_the_same_shapes_are_caught(string a, string b) => Collide(a, b);

    [Theory]
    // Genuinely different subjects that share a word must survive, or the generator starves.
    [InlineData("API versioning", "API pagination methods")]
    [InlineData("PostgreSQL indexing", "PostgreSQL advantages")]
    [InlineData("Connection pooling", "Connection lifetime")]
    [InlineData("SQL vs NoSQL trade-offs", "SQL query optimization")]
    [InlineData("Database migrations in production", "Database scaling")]
    [InlineData("Error handling in APIs", "Rate limiting")]
    public void Genuinely_distinct_topics_do_not_collide(string a, string b) => Distinct(a, b);

    [Fact]
    public void Case_and_punctuation_do_not_matter()
    {
        Collide("SQL Query Optimization", "sql query optimization");
        Collide("trade-offs: SQL vs NoSQL", "SQL versus NoSQL trade offs");
    }

    [Fact]
    public void An_empty_topic_never_collides()
    {
        // An empty key is a subset of everything; treating it as a collision would block every
        // question the moment one untopiced row existed.
        Assert.False(TopicKey.Collides("", "connection pooling"));
        Assert.False(TopicKey.Collides("   ", "connection pooling"));
        Assert.False(TopicKey.Collides(null, "connection pooling"));
        Assert.False(TopicKey.Collides("vs and the", "connection pooling"));   // all stopwords
    }

    [Fact]
    public void Subject_words_are_not_treated_as_filler()
    {
        // "database", "API" and "SQL" carry the subject. Stopwording them would collapse the whole space.
        Assert.Contains("database", TopicKey.Tokens("database migrations"));
        Assert.Contains("api", TopicKey.Tokens("API versioning"));
        Assert.Contains("sql", TopicKey.Tokens("SQL joins"));
    }

    [Fact]
    public void CollidesWithAny_finds_one_match_in_a_list()
    {
        var stored = new[] { "connection pooling", "API versioning", "ORM vs raw SQL" };

        Assert.True(TopicKey.CollidesWithAny("Raw SQL vs ORM", stored));
        Assert.False(TopicKey.CollidesWithAny("materialized views", stored));
    }
}
