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

    // ---- Review Critical 1: a null element in the array must not crash ----

    [Fact]
    public void A_null_element_in_the_array_is_skipped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """[null, { "id": "kei", "name": "Kei cars", "cars": ["x2a8n"] }]""");

        Assert.True(catalogue.TryFind("kei", out _));
        Assert.Equal(CarTable.Arcade.Count + 1, catalogue.Groups.Count);
    }

    [Fact]
    public void An_array_that_is_only_a_null_leaves_just_the_built_in_groups()
    {
        var catalogue = CarCatalogue.FromJson(Database(), "[null]");

        Assert.Equal(
            CarTable.Arcade.Select(g => g.Id),
            catalogue.Groups.Select(g => g.Id));
    }

    // ---- Review Critical 2: a null or blank car code must not crash or reach the wire ----

    [Fact]
    public void A_null_car_code_is_dropped_with_a_database()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "kei", "name": "Kei cars", "cars": [null, "x2a8n"] }]""");

        Assert.True(catalogue.TryFind("kei", out var group));
        Assert.Equal(new[] { "x2a8n" }, group.Cars);
    }

    [Fact]
    public void A_null_or_blank_car_code_is_dropped_without_a_database()
    {
        var catalogue = CarCatalogue.FromJson(null,
            """[{ "id": "kei", "name": "Kei cars", "cars": [null, "  ", "x2a8n"] }]""");

        Assert.True(catalogue.TryFind("kei", out var group));
        Assert.Equal(new[] { "x2a8n" }, group.Cars);
    }

    [Fact]
    public void A_group_left_with_only_null_car_codes_is_dropped()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "ghost", "name": "Ghosts", "cars": [null, "  "] }]""");

        Assert.False(catalogue.TryFind("ghost", out _));
    }

    // ---- Minor 10: duplicate ids within one config file ----

    [Fact]
    public void The_last_of_two_custom_entries_sharing_an_id_wins()
    {
        var catalogue = CarCatalogue.FromJson(Database(),
            """
            [{ "id": "kei", "name": "First",  "cars": ["x2a8n"] },
             { "id": "kei", "name": "Second", "cars": ["q2mcn"] }]
            """);

        Assert.Equal(CarTable.Arcade.Count + 1, catalogue.Groups.Count);   // appended once, not twice
        Assert.True(catalogue.TryFind("kei", out var group));
        Assert.Equal("Second", group.Name);
        Assert.Equal(new[] { "q2mcn" }, group.Cars);
    }

    // ---- Minor 11: ids merge case-insensitively, but the built-in id sticks ----

    [Fact]
    public void An_id_differing_only_by_case_replaces_the_built_in_group_instead_of_joining_it()
    {
        int position = CarTable.Arcade.ToList().FindIndex(g => g.Id == "c");

        var catalogue = CarCatalogue.FromJson(Database(),
            """[{ "id": "C", "name": "Class C (house)", "cars": ["h2s2n"] }]""");

        Assert.Equal(CarTable.Arcade.Count, catalogue.Groups.Count);      // no sixth group
        Assert.Equal("c", catalogue.Groups[position].Id);                 // the built-in id survives, not "C"
        Assert.Equal("Class C (house)", catalogue.Groups[position].Name);
        Assert.True(catalogue.TryFind("c", out _));
    }

    // ---- Minor 13: an id that cannot survive the wire is dropped, not truncated ----

    [Fact]
    public void A_group_whose_id_is_too_long_for_the_wire_is_dropped()
    {
        var longId = new string('x', RoomState.MaxStringBytes + 1);

        var catalogue = CarCatalogue.FromJson(Database(),
            $$"""[{ "id": "{{longId}}", "name": "Too long", "cars": ["x2a8n"] }]""");

        Assert.False(catalogue.TryFind(longId, out _));
        Assert.Equal(
            CarTable.Arcade.Select(g => g.Id),
            catalogue.Groups.Select(g => g.Id));
    }

    // ---- Minor 16: CarCatalogue.Load, the file-reading half ----

    [Fact]
    public void Load_reads_a_config_file_that_exists()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """[{ "id": "kei", "name": "Kei cars", "cars": ["x2a8n"] }]""");

            var catalogue = CarCatalogue.Load(Database(), path);

            Assert.True(catalogue.TryFind("kei", out var group));
            Assert.Equal(new[] { "x2a8n" }, group.Cars);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_falls_back_to_the_built_in_groups_when_the_path_does_not_exist()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        var catalogue = CarCatalogue.Load(Database(), path);

        Assert.Equal(
            CarTable.Arcade.Select(g => g.Id),
            catalogue.Groups.Select(g => g.Id));
    }

    [Fact]
    public void Load_does_not_throw_when_the_path_is_a_directory()
    {
        var catalogue = CarCatalogue.Load(Database(), Path.GetTempPath());

        Assert.Equal(
            CarTable.Arcade.Select(g => g.Id),
            catalogue.Groups.Select(g => g.Id));
    }

    [Fact]
    public void Load_with_no_config_path_is_the_built_in_groups()
    {
        var catalogue = CarCatalogue.Load(Database(), null);

        Assert.Equal(
            CarTable.Arcade.Select(g => g.Id),
            catalogue.Groups.Select(g => g.Id));
    }
}
