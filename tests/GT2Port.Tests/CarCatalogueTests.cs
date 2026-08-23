using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class CarCatalogueTests
{
    // A database holding just the cars these tests name. CarInfo's own tests
    // cover parsing; this only needs it to answer "is this a real car".
    static CarInfo Database() => CarInfoTests.BuildDatabaseFor(
        "ldvan", "dvpgn", "x2a8n", "q2mcn", "h2s2n");

    [Fact]
    public void Without_a_config_file_the_catalogue_is_the_arcade_groups()
    {
        var catalogue = CarCatalogue.FromJson(Database(), null);

        Assert.Equal(
            CarTable.Arcade.Select(g => g.Id),
            catalogue.Groups.Select(g => g.Id));
    }

    [Fact]
    public void A_new_group_is_appended_after_the_built_in_ones()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n", "q2mcn"] }]""");

        Assert.Equal(CarTable.Arcade.Count + 1, catalogue.Groups.Count);
        Assert.Equal("kei", catalogue.Groups[^1].Id);
        Assert.Equal(new[] { "x2a8n", "q2mcn" }, catalogue.Groups[^1].Cars);
    }

    [Fact]
    public void A_matching_id_replaces_the_built_in_group_in_place()
    {
        int position = CarTable.Arcade.ToList().FindIndex(g => g.Id == "c");

        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "c", "name": "Class C (house)", "cars": ["h2s2n"] }]""");

        Assert.Equal(CarTable.Arcade.Count, catalogue.Groups.Count);
        Assert.Equal("Class C (house)", catalogue.Groups[position].Name);
        Assert.Equal(new[] { "h2s2n" }, catalogue.Groups[position].Cars);
    }

    [Fact]
    public void A_car_the_database_does_not_know_is_dropped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n", "nosuch", "q2mcn"] }]""");

        Assert.True(catalogue.TryFind("kei", out var group));
        Assert.Equal(new[] { "x2a8n", "q2mcn" }, group.Cars);
    }

    [Fact]
    public void A_group_left_with_no_cars_is_dropped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """
            [{ "id": "kei",   "name": "Kei cars", "cars": ["x2a8n"] },
             { "id": "ghost", "name": "Ghosts",   "cars": ["nosuch"] }]
            """);

        Assert.True(catalogue.TryFind("kei", out _));     // the file was read
        Assert.False(catalogue.TryFind("ghost", out _));
    }

    [Fact]
    public void Nothing_is_dropped_when_there_is_no_database()
    {
        var catalogue = CarCatalogue.FromJson(null,
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n", "nosuch"] }]""");

        Assert.True(catalogue.TryFind("kei", out var group));
        Assert.Equal(new[] { "x2a8n", "nosuch" }, group.Cars);
    }

    [Fact]
    public void A_malformed_file_is_ignored_whole()
    {
        var good = CarCatalogue.FromJson(Database(),
            """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n"] }]""");
        Assert.True(good.TryFind("kei", out _));          // valid JSON is read

        var catalogue = CarCatalogue.FromJson(Database(), "{ not json at all");

        Assert.Equal(CarTable.Arcade.Count, catalogue.Groups.Count);
    }

    [Fact]
    public void A_group_with_a_blank_id_or_name_is_dropped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """
            [{ "id": "",    "name": "Nameless", "cars": ["x2a8n"] },
             { "id": "ok",  "name": "  ",       "cars": ["x2a8n"] },
             { "id": "kei", "name": "Kei cars", "cars": ["x2a8n"] }]
            """);

        Assert.True(catalogue.TryFind("kei", out _));     // the file was read
        Assert.Equal(CarTable.Arcade.Count + 1, catalogue.Groups.Count);
    }

    [Fact]
    public void An_unknown_group_is_not_found()
    {
        var catalogue = CarCatalogue.FromJson(Database(), null);

        Assert.True(catalogue.TryFind("special", out _));
        Assert.False(catalogue.TryFind("nosuchgroup", out _));
    }
}
