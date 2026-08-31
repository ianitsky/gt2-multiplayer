using GT2Port.Multiplayer;
using Xunit;

using Arrival = GT2Port.Multiplayer.BackToTheLobby.Arrival;

namespace GT2Port.Tests;

/// <summary>
/// The end of a race is the next overlay to arrive after the race's own. These
/// pin the one thing that rule can get wrong: the arcade loads several overlays
/// while it sets a race up, so only the first arrival after the race counts.
/// Reading a later one as another ending would run the lobby again in the
/// middle of starting a race.
/// </summary>
public class BackToTheLobbyTests
{
    const uint RaceOverlay = 0x80011F64u;
    const uint Arcade = 0x80011750u;
    const uint Something = 0x80013628u;   // Simulation, which a race never ends into

    [Fact]
    public void The_race_overlay_arriving_is_a_race_starting()
    {
        Assert.Equal(Arrival.ARaceIsStarting, BackToTheLobby.WhatItMeans(racing: false, RaceOverlay));
        Assert.Equal(Arrival.ARaceIsStarting, BackToTheLobby.WhatItMeans(racing: true, RaceOverlay));
    }

    [Fact]
    public void The_arcade_after_a_race_is_the_room_waiting()
    {
        Assert.Equal(
            Arrival.TheRaceIsOverAndTheRoomIsThere,
            BackToTheLobby.WhatItMeans(racing: true, Arcade));
    }

    /// <summary>
    /// The arcade is also what loads a race. Without a race behind it, its
    /// arrival is the ordinary one and means nothing - which is what stops the
    /// lobby running again on every overlay the arcade fetches.
    /// </summary>
    [Fact]
    public void The_arcade_without_a_race_behind_it_means_nothing()
    {
        Assert.Equal(Arrival.Nothing, BackToTheLobby.WhatItMeans(racing: false, Arcade));
        Assert.Equal(Arrival.Nothing, BackToTheLobby.WhatItMeans(racing: false, Something));
    }

    /// <summary>
    /// A race ending into anything else is still a race ending, and is reported
    /// rather than acted on - the log line is what would name the overlay to
    /// return from if the arcade turns out not to be it.
    /// </summary>
    [Fact]
    public void A_race_ending_into_something_else_is_still_the_end_of_one()
    {
        Assert.Equal(
            Arrival.TheRaceIsOverSomewhereElse,
            BackToTheLobby.WhatItMeans(racing: true, Something));
    }
}
