using GT2Port.Multiplayer;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// What CarLoad can be held to without the game running.
///
/// The call it makes is the game's own function, so the asking itself needs a
/// loaded funcmap and cannot be reached from here. Everything around it can:
/// that it refuses to ask before it knows the owner, that it reads the request
/// slots where the owner keeps them, and that it can tell a request still
/// working from one that has run out of steps - which is what a caller waits
/// on before starting a race.
/// </summary>
public class CarLoadTests
{
    /// <summary>Where a real tick was seen to put the owner - on the main stack.</summary>
    const uint Owner = 0x801FF9F0u;

    /// <summary>The first of the two static request records gt2_03 installs.</summary>
    const uint Request = 0x800EF4A8u;

    const int FirstSlot = 0x228;
    const int Step = 0x10;

    static (CpuContext, PSMemory) Fresh()
    {
        CarLoad.Forget();
        return (new CpuContext(), new PSMemory());
    }

    static void Tick(CpuContext c, PSMemory m, uint owner)
    {
        c.A1 = owner;
        CarLoad.Ticking(c, m);
    }

    [Fact]
    public void Knows_nothing_until_a_tick_names_the_owner()
    {
        var (c, m) = Fresh();

        Assert.False(CarLoad.Ready);
        Assert.Equal(0u, CarLoad.RequestIn(m, 0));
        Assert.False(CarLoad.TryAsk(c, m, 0, "ccrcn"));
    }

    [Fact]
    public void Reads_the_request_slots_off_the_owner()
    {
        var (c, m) = Fresh();
        m.WriteU32(Owner + FirstSlot, Request);
        m.WriteU32(Owner + FirstSlot + 4, Request + 0x448u);

        Tick(c, m, Owner);

        Assert.True(CarLoad.Ready);
        Assert.Equal(Request, CarLoad.RequestIn(m, 0));
        Assert.Equal(Request + 0x448u, CarLoad.RequestIn(m, 1));
    }

    [Fact]
    public void Has_no_slot_beyond_the_two_the_arcade_prepares()
    {
        var (c, m) = Fresh();
        m.WriteU32(Owner + FirstSlot, Request);
        Tick(c, m, Owner);

        Assert.Equal(0u, CarLoad.RequestIn(m, -1));
        Assert.Equal(0u, CarLoad.RequestIn(m, CarLoad.Slots));
    }

    [Fact]
    public void Refuses_a_code_that_is_not_a_car_id()
    {
        var (c, m) = Fresh();
        m.WriteU32(Owner + FirstSlot, Request);
        Tick(c, m, Owner);

        // Five characters is the whole of an id, and the alphabet has no room
        // for anything outside "-0123456789a-z".
        Assert.False(CarLoad.TryAsk(c, m, 0, ""));
        Assert.False(CarLoad.TryAsk(c, m, 0, "TOO LONG"));
    }

    [Fact]
    public void A_request_partway_through_its_steps_is_not_done()
    {
        var (c, m) = Fresh();
        m.WriteU32(Owner + FirstSlot, Request);
        Tick(c, m, Owner);

        m.WriteU8(Request + Step, 2);      // reading the model
        Assert.False(CarLoad.DoneIn(m, 0));

        m.WriteU8(Request + Step, 4);      // reading the textures
        Assert.False(CarLoad.DoneIn(m, 0));
    }

    [Fact]
    public void A_request_out_of_steps_is_done()
    {
        var (c, m) = Fresh();
        m.WriteU32(Owner + FirstSlot, Request);
        Tick(c, m, Owner);

        m.WriteU8(Request + Step, 9);
        Assert.True(CarLoad.DoneIn(m, 0));
    }

    [Fact]
    public void A_slot_never_asked_for_anything_is_not_something_to_wait_on()
    {
        var (c, m) = Fresh();
        m.WriteU32(Owner + FirstSlot, Request);
        Tick(c, m, Owner);

        // Step zero is what func_80016234 leaves behind: the request exists
        // but has not been set going, so waiting for it would wait for ever.
        m.WriteU8(Request + Step, 0);
        Assert.True(CarLoad.DoneIn(m, 0));

        // And a slot with no request at all - the second one, here.
        Assert.True(CarLoad.DoneIn(m, 1));
    }
}
