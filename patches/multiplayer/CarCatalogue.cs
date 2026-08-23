using System.Text.Json;
using System.Text.Json.Serialization;

namespace GT2Port.Multiplayer;

/// <summary>
/// Decides which car groups the lobby's picker actually offers. The arcade
/// five (<see cref="CarTable.Arcade"/>) are a default, not a fixed set: a
/// hand-written JSON file can add groups of its own or replace a built-in one
/// outright, because a group is nothing more than an id, a display name, and
/// a list of car codes drawn from the disc's own database. In the same spirit
/// as <see cref="CourseTable"/> and <see cref="VolArchive"/>: nothing here
/// throws, and a config file that does not parse is ignored whole rather than
/// keeping the lobby from opening.
/// </summary>
public sealed class CarCatalogue
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    readonly CarInfo? _info;
    readonly Dictionary<string, CarGroup> _byId;

    public IReadOnlyList<CarGroup> Groups { get; }

    CarCatalogue(CarInfo? info, IReadOnlyList<CarGroup> groups)
    {
        _info = info;
        Groups = groups;
        _byId = groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// Loads the catalogue: the arcade groups merged with the file at
    /// <paramref name="configPath"/>, when one is given and exists. Never
    /// throws - a config file that cannot be read is treated exactly like a
    /// missing one.
    /// </summary>
    public static CarCatalogue Load(CarInfo? info, string? configPath)
    {
        string? json = null;
        if (!string.IsNullOrEmpty(configPath))
        {
            try
            {
                if (File.Exists(configPath))
                    json = File.ReadAllText(configPath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CarCatalogue] could not read '{configPath}' ({e.Message}); using built-in groups only");
                json = null;
            }
        }

        return FromJson(info, json);
    }

    /// <summary>Merges the arcade groups with <paramref name="json"/> already in memory. Internal seam for tests.</summary>
    internal static CarCatalogue FromJson(CarInfo? info, string? json)
    {
        var groups = CarTable.Arcade.ToList();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < groups.Count; i++) indexById[groups[i].Id] = i;

        if (!string.IsNullOrWhiteSpace(json))
        {
            List<GroupDto>? custom;
            try
            {
                custom = JsonSerializer.Deserialize<List<GroupDto>>(json, JsonOptions);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CarCatalogue] config file is not valid JSON ({e.Message}); using built-in groups only");
                custom = null;
            }

            if (custom is not null)
            {
                foreach (var dto in custom)
                {
                    if (!TryBuildGroup(info, dto, out var group)) continue;

                    if (indexById.TryGetValue(group.Id, out int at))
                        groups[at] = group;
                    else
                    {
                        indexById[group.Id] = groups.Count;
                        groups.Add(group);
                    }
                }
            }
        }

        return new CarCatalogue(info, groups);
    }

    /// <summary>
    /// Builds one group from its DTO: drops cars the database does not know
    /// about (unless there is no database to check against), then drops the
    /// whole group if that leaves it with no cars, or if its id or name is
    /// blank.
    /// </summary>
    static bool TryBuildGroup(CarInfo? info, GroupDto dto, out CarGroup group)
    {
        group = null!;

        string id = (dto.Id ?? "").Trim();
        string name = (dto.Name ?? "").Trim();
        if (id.Length == 0 || name.Length == 0) return false;

        var source = dto.Cars ?? [];
        var cars = new List<string>(source.Count);
        foreach (var code in source)
        {
            if (info is not null && !info.TryName(code, out _))
            {
                Console.WriteLine($"[CarCatalogue] dropping unknown car '{code}' from group '{id}'");
                continue;
            }
            cars.Add(code);
        }
        if (cars.Count == 0) return false;

        group = new CarGroup(id, name, cars);
        return true;
    }

    public bool TryFind(string groupId, out CarGroup group) => _byId.TryGetValue(groupId, out group!);

    /// <summary>
    /// The name to show for a car code. An unknown code returns itself,
    /// mirroring <see cref="CourseTable.DisplayName"/> and <see cref="CarInfo.DisplayName"/>.
    /// </summary>
    public string DisplayName(string carCode) => _info?.DisplayName(carCode) ?? carCode;

    sealed record GroupDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("cars")] List<string>? Cars);
}
