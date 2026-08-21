# VBlank Interrupt Delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the recompiled GT2 deliver VBlank interrupts to its registered handler, so `gt2_sysinit_vsync_setup`'s busy-wait on `0x80011DF4` completes and the boot proceeds past `gt2_sysinit`.

**Architecture:** A timer thread raises a pending count; the game thread delivers it at safepoints the recompiler injects before every backward branch. The handler runs on a fresh `CpuContext` with `SP = game SP - 512`. Delivery also pumps the host window, which is what unfreezes the white screen.

**Tech Stack:** C# / .NET 10, xunit, RecompOne runtime + recompiler (our fork).

## Global Constraints

- Target framework is `net10.0` for every project, matching `RecompOne.Runtime.csproj`.
- Only VBlank is wired. Do not implement DMA, RCnt/timers, or I_STAT/I_MASK.
- The game runs on the main thread. The only new thread is the VBlank timer, and it must touch neither `CpuContext` nor `IMemory` — it may only increment the pending counter.
- Pending VBlanks accumulate as a counter capped at 4, never a bool.
- Safepoint emission is behind the recompiler config flag `"safepoints"`, defaulting to `true`.
- Reference spec: `docs/superpowers/specs/2026-08-21-vblank-interrupt-delivery-design.md`.
- Commit after every task. Never use `--no-verify`.

---

### Task 0: Point the submodule at our fork

This task is the only one requiring GitHub actions the automation cannot perform. It must be done first: every later task edits files under `RecompOne/`, and those edits are unpushable until the submodule points somewhere we can write.

**Files:**
- Modify: `.gitmodules`

**Interfaces:**
- Consumes: nothing.
- Produces: a writable `RecompOne` submodule on branch `vblank-interrupts`.

- [ ] **Step 1: Create the fork (human action)**

On GitHub, fork `BlackLabelHQ/RecompOne`. The fork keeps upstream history, so the submodule commit currently checked out remains valid.

- [ ] **Step 2: Repoint the submodule**

Replace `<you>` with the GitHub account that owns the fork:

```bash
git config -f .gitmodules submodule.RecompOne.url https://github.com/<you>/RecompOne.git
git submodule sync RecompOne
git -C RecompOne remote set-url origin https://github.com/<you>/RecompOne.git
git -C RecompOne remote add upstream https://github.com/BlackLabelHQ/RecompOne.git
```

- [ ] **Step 3: Verify the remotes**

```bash
git -C RecompOne remote -v
```

Expected: `origin` points at the fork, `upstream` at `BlackLabelHQ/RecompOne`.

- [ ] **Step 4: Create the working branch in the fork**

```bash
git -C RecompOne checkout -b vblank-interrupts
```

- [ ] **Step 5: Commit**

```bash
git add .gitmodules
git commit -m "Point RecompOne submodule at our fork"
```

---

### Task 1: InterruptController

The core state machine, with no dependency on the emitter, the host, or the game. Everything else in the plan builds on it, so it is written and proven first.

**Files:**
- Create: `RecompOne/RecompOne.Runtime/Interrupts/InterruptController.cs`
- Create: `tests/GT2Port.Tests/GT2Port.Tests.csproj`
- Create: `tests/GT2Port.Tests/InterruptControllerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `RecompOne.Runtime.Interrupts.InterruptController` (static class)
  - `static void Raise()` — increments pending, saturating at `MaxPending`
  - `static int Pending { get; }`
  - `static int CriticalDepth { get; }`
  - `static void EnterCritical()` / `static void ExitCritical()` — `ExitCritical` never drives depth below zero
  - `static bool Delivering { get; }`
  - `static void SetHandler(uint address)` / `static uint Handler { get; }` — `0` means unregistered
  - `static int Drain()` — returns how many deliveries are owed and clears pending; returns `0` when `CriticalDepth > 0`, when `Delivering` is true, or when `Handler == 0`
  - `static IDisposable BeginDelivery()` — sets `Delivering` for the scope
  - `static void Reset()` — test hook clearing all state
  - `const int MaxPending = 4`

- [ ] **Step 1: Create the test project**

```bash
mkdir -p tests/GT2Port.Tests
```

Create `tests/GT2Port.Tests/GT2Port.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
        <PackageReference Include="xunit" Version="2.9.2" />
        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="../../RecompOne/RecompOne.Runtime/RecompOne.Runtime.csproj" />
    </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the failing tests**

Create `tests/GT2Port.Tests/InterruptControllerTests.cs`:

```csharp
using RecompOne.Runtime.Interrupts;
using Xunit;

namespace GT2Port.Tests;

[Collection("interrupts")]
public class InterruptControllerTests
{
    public InterruptControllerTests() => InterruptController.Reset();

    [Fact]
    public void Drain_returns_nothing_when_no_handler_is_registered()
    {
        InterruptController.Raise();
        Assert.Equal(0, InterruptController.Drain());
    }

    [Fact]
    public void Raised_interrupt_is_delivered_once()
    {
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();

        Assert.Equal(1, InterruptController.Drain());
        Assert.Equal(0, InterruptController.Drain());
    }

    [Fact]
    public void Critical_section_defers_delivery_until_it_closes()
    {
        InterruptController.SetHandler(0x80010928u);
        InterruptController.EnterCritical();
        InterruptController.Raise();

        Assert.Equal(0, InterruptController.Drain());

        InterruptController.ExitCritical();
        Assert.Equal(1, InterruptController.Drain());
    }

    [Fact]
    public void Nested_critical_sections_need_matching_exits()
    {
        InterruptController.SetHandler(0x80010928u);
        InterruptController.EnterCritical();
        InterruptController.EnterCritical();
        InterruptController.Raise();
        InterruptController.ExitCritical();

        Assert.Equal(0, InterruptController.Drain());

        InterruptController.ExitCritical();
        Assert.Equal(1, InterruptController.Drain());
    }

    [Fact]
    public void Unbalanced_exit_does_not_drive_depth_negative()
    {
        InterruptController.ExitCritical();
        Assert.Equal(0, InterruptController.CriticalDepth);

        InterruptController.EnterCritical();
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();
        Assert.Equal(0, InterruptController.Drain());
    }

    [Fact]
    public void Drain_does_not_reenter_while_delivering()
    {
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();

        using (InterruptController.BeginDelivery())
        {
            InterruptController.Raise();
            Assert.Equal(0, InterruptController.Drain());
        }

        Assert.Equal(1, InterruptController.Drain());
    }

    [Fact]
    public void Three_pending_interrupts_are_owed_three_deliveries()
    {
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();
        InterruptController.Raise();
        InterruptController.Raise();

        Assert.Equal(3, InterruptController.Drain());
    }

    [Fact]
    public void Pending_saturates_at_the_cap()
    {
        InterruptController.SetHandler(0x80010928u);
        for (int i = 0; i < 20; i++) InterruptController.Raise();

        Assert.Equal(InterruptController.MaxPending, InterruptController.Drain());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: build failure — `RecompOne.Runtime.Interrupts` does not exist.

- [ ] **Step 4: Write the implementation**

Create `RecompOne/RecompOne.Runtime/Interrupts/InterruptController.cs`:

```csharp
namespace RecompOne.Runtime.Interrupts;

/// <summary>
/// Tracks pending hardware interrupts and decides when the game may service them.
///
/// The game polls this at safepoints the recompiler injects before backward
/// branches, so delivery always happens on the game thread between two
/// instructions - the same place a real CPU takes an interrupt. Only Raise() is
/// called from another thread.
/// </summary>
public static class InterruptController
{
    public const int MaxPending = 4;

    static int _pending;
    static int _criticalDepth;
    static uint _handler;

    [ThreadStatic] static bool _delivering;

    public static int Pending => Volatile.Read(ref _pending);
    public static int CriticalDepth => _criticalDepth;
    public static bool Delivering => _delivering;
    public static uint Handler => _handler;

    /// <summary>Called from the timer thread. Saturates rather than queueing without bound.</summary>
    public static void Raise()
    {
        int seen = Volatile.Read(ref _pending);
        while (seen < MaxPending)
        {
            int prior = Interlocked.CompareExchange(ref _pending, seen + 1, seen);
            if (prior == seen) return;
            seen = prior;
        }
    }

    public static void SetHandler(uint address) => _handler = address;

    public static void EnterCritical() => _criticalDepth++;

    public static void ExitCritical()
    {
        if (_criticalDepth > 0) _criticalDepth--;
    }

    /// <summary>
    /// Returns how many handler runs are owed, clearing the pending count.
    /// Returns zero when servicing is not permitted right now.
    /// </summary>
    public static int Drain()
    {
        if (_handler == 0) return 0;
        if (_criticalDepth > 0) return 0;
        if (_delivering) return 0;
        return Interlocked.Exchange(ref _pending, 0);
    }

    public static IDisposable BeginDelivery() => new DeliveryScope();

    public static void Reset()
    {
        Volatile.Write(ref _pending, 0);
        _criticalDepth = 0;
        _handler = 0;
        _delivering = false;
    }

    sealed class DeliveryScope : IDisposable
    {
        public DeliveryScope() => _delivering = true;
        public void Dispose() => _delivering = false;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: PASS, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add tests/GT2Port.Tests RecompOne/RecompOne.Runtime/Interrupts/InterruptController.cs
git commit -m "Add InterruptController with pending, critical-section and reentrancy rules"
```

---

### Task 2: Irq facade, host pump, and the VBlank source

Wires the controller to the host: the facade the generated code will call, the window pump that unfreezes the screen, and the timer that raises VBlank.

`HostWindow.Pump()` already exists at `RecompOne/RecompOne.Runtime/Host/Window/HostWindow.cs:271` and does exactly what delivery needs (`DoEvents`, close check, `DoRender`) without presenting a game frame. It is `internal`, and `Runtime` is in the same assembly, so `Runtime.PumpHost()` can call it directly.

**Files:**
- Create: `RecompOne/RecompOne.Runtime/Interrupts/IVBlankSource.cs`
- Create: `RecompOne/RecompOne.Runtime/Interrupts/WallClockVBlankSource.cs`
- Create: `RecompOne/RecompOne.Runtime/Interrupts/Irq.cs`
- Modify: `RecompOne/RecompOne.Runtime/Runtime.cs` (add `PumpHost` above `PresentFrame`, near line 148)
- Create: `tests/GT2Port.Tests/IrqTests.cs`

**Interfaces:**
- Consumes: `InterruptController` from Task 1.
- Produces:
  - `RecompOne.Runtime.Interrupts.IVBlankSource` — `void Start(Action raise)`, `void Stop()`
  - `RecompOne.Runtime.Interrupts.WallClockVBlankSource` — `WallClockVBlankSource(double hz = 59.94)`
  - `RecompOne.Runtime.Interrupts.Irq` — `static void Poll(CpuContext c, IMemory m)`, `static Action<CpuContext, IMemory, uint>? Invoke`, `static Action? PumpHost`, `static void Reset()`
  - `RecompOne.Runtime.Runtime.PumpHost()`

`Irq.Invoke` and `Irq.PumpHost` are settable hooks rather than direct calls to `Dispatcher` and `HostWindow` so the safepoint can be tested without a window or a loaded overlay. `Program.cs` wires them to the real implementations in Task 5.

- [ ] **Step 1: Write the failing tests**

Create `tests/GT2Port.Tests/IrqTests.cs`:

```csharp
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

[Collection("interrupts")]
public class IrqTests
{
    public IrqTests()
    {
        InterruptController.Reset();
        Irq.Reset();
    }

    static (CpuContext, IMemory) Fixture()
    {
        var c = new CpuContext { SP = 0x801FFF00u, GP = 0x12345678u };
        return (c, new PSMemory());
    }

    [Fact]
    public void Poll_does_nothing_when_no_interrupt_is_pending()
    {
        var (c, m) = Fixture();
        int calls = 0;
        Irq.Invoke = (_, _, _) => calls++;

        Irq.Poll(c, m);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Poll_runs_the_handler_on_a_separate_context_below_the_game_stack()
    {
        var (c, m) = Fixture();
        CpuContext? seen = null;
        Irq.Invoke = (ctx, _, _) => seen = ctx;
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.NotNull(seen);
        Assert.NotSame(c, seen);
        Assert.Equal(c.SP - 512u, seen!.SP);
        Assert.Equal(c.GP, seen.GP);
    }

    [Fact]
    public void Poll_passes_the_registered_handler_address()
    {
        var (c, m) = Fixture();
        uint target = 0;
        Irq.Invoke = (_, _, addr) => target = addr;
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(0x80010928u, target);
    }

    [Fact]
    public void Poll_leaves_the_game_context_untouched()
    {
        var (c, m) = Fixture();
        Irq.Invoke = (ctx, _, _) => { ctx.V0 = 0xDEADBEEFu; ctx.SP -= 64u; };
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(0u, c.V0);
        Assert.Equal(0x801FFF00u, c.SP);
    }

    [Fact]
    public void Poll_runs_the_handler_once_per_pending_interrupt()
    {
        var (c, m) = Fixture();
        int calls = 0;
        Irq.Invoke = (_, _, _) => calls++;
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();
        InterruptController.Raise();
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(3, calls);
    }

    [Fact]
    public void A_handler_that_polls_again_does_not_reenter()
    {
        var (c, m) = Fixture();
        int calls = 0;
        Irq.Invoke = (_, _, _) =>
        {
            calls++;
            InterruptController.Raise();
            Irq.Poll(c, m);
        };
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Poll_pumps_the_host_before_running_the_handler()
    {
        var (c, m) = Fixture();
        var order = new List<string>();
        Irq.PumpHost = () => order.Add("pump");
        Irq.Invoke = (_, _, _) => order.Add("handler");
        InterruptController.SetHandler(0x80010928u);
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(new[] { "pump", "handler" }, order);
    }

    [Fact]
    public void Wall_clock_source_raises_over_time()
    {
        int raised = 0;
        var source = new WallClockVBlankSource(hz: 1000.0);
        source.Start(() => Interlocked.Increment(ref raised));

        Thread.Sleep(200);
        source.Stop();

        Assert.True(raised > 0, $"expected the source to raise at least once, saw {raised}");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: build failure — `Irq` and `WallClockVBlankSource` do not exist.

- [ ] **Step 3: Write IVBlankSource**

Create `RecompOne/RecompOne.Runtime/Interrupts/IVBlankSource.cs`:

```csharp
namespace RecompOne.Runtime.Interrupts;

/// <summary>
/// Decides when a VBlank happens. Wall-clock today; netcode supplies its own
/// implementation later so it can own frame advance for lockstep.
/// </summary>
public interface IVBlankSource
{
    void Start(Action raise);
    void Stop();
}
```

- [ ] **Step 4: Write WallClockVBlankSource**

Create `RecompOne/RecompOne.Runtime/Interrupts/WallClockVBlankSource.cs`:

```csharp
namespace RecompOne.Runtime.Interrupts;

/// <summary>
/// Raises VBlank on a timer thread. The callback must only bump a counter -
/// this thread never touches CpuContext or memory.
/// </summary>
public sealed class WallClockVBlankSource : IVBlankSource
{
    readonly double _hz;
    CancellationTokenSource? _cts;
    Thread? _thread;

    public WallClockVBlankSource(double hz = 59.94) => _hz = hz;

    public void Start(Action raise)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var period = TimeSpan.FromSeconds(1.0 / _hz);

        _thread = new Thread(() =>
        {
            var next = DateTime.UtcNow + period;
            while (!cts.IsCancellationRequested)
            {
                var wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                raise();
                next += period;
                // A stalled host must not make this thread spin trying to catch up.
                var now = DateTime.UtcNow;
                if (next < now) next = now + period;
            }
        })
        {
            IsBackground = true,
            Name = "vblank",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromMilliseconds(250));
        _cts?.Dispose();
        _cts = null;
        _thread = null;
    }
}
```

- [ ] **Step 5: Write the Irq facade**

Create `RecompOne/RecompOne.Runtime/Interrupts/Irq.cs`:

```csharp
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Interrupts;

/// <summary>
/// The safepoint the generated code calls before every backward branch.
/// Poll() is kept to a field read and a compare on the common path so it
/// inlines and costs effectively nothing per loop iteration.
/// </summary>
public static class Irq
{
    /// <summary>Dispatches the handler. Wired to Dispatcher.Call at startup.</summary>
    public static Action<CpuContext, IMemory, uint>? Invoke;

    /// <summary>Pumps window events. Wired to Runtime.PumpHost at startup.</summary>
    public static Action? PumpHost;

    public static void Poll(CpuContext c, IMemory m)
    {
        if (InterruptController.Pending == 0) return;
        Service(c, m);
    }

    static void Service(CpuContext c, IMemory m)
    {
        int owed = InterruptController.Drain();
        if (owed == 0) return;

        uint handler = InterruptController.Handler;
        using (InterruptController.BeginDelivery())
        {
            PumpHost?.Invoke();

            for (int i = 0; i < owed; i++)
            {
                // A fresh context: the game is mid-function with live registers,
                // and anything below its stack pointer is free space.
                var handlerCtx = new CpuContext
                {
                    SP = c.SP - 512u,
                    GP = c.GP,
                    RA = 0u,
                };
                handlerCtx.FP = handlerCtx.SP;
                Invoke?.Invoke(handlerCtx, m, handler);
            }
        }
    }

    public static void Reset()
    {
        Invoke = null;
        PumpHost = null;
    }
}
```

- [ ] **Step 6: Add Runtime.PumpHost**

In `RecompOne/RecompOne.Runtime/Runtime.cs`, add this method directly above the existing `PresentFrame` method (around line 148):

```csharp
    /// <summary>
    /// Pumps window events without presenting a game frame. Interrupt delivery
    /// calls this so the window stays responsive while the game is inside a
    /// busy-wait that never reaches libetc's VSync.
    /// </summary>
    public static void PumpHost() => HostWindow.Pump();
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: PASS, 16 tests.

- [ ] **Step 8: Commit**

```bash
git add RecompOne/RecompOne.Runtime/Interrupts RecompOne/RecompOne.Runtime/Runtime.cs tests/GT2Port.Tests/IrqTests.cs
git commit -m "Add Irq safepoint, wall-clock VBlank source and host pump"
```

---

### Task 3: LibApi reimplementations

The game registers its handler through `VSyncCallback` and guards regions with `EnterCriticalSection` / `ExitCriticalSection`. All three are currently game code the runtime ignores. Reimplementing them is what connects the game's handler to `InterruptController`.

`VSyncCallback` receives the handler address in `A0` (confirmed in the generated `gt2_sysinit_vsync_setup`, which loads `0x80010928` into `A0` before the call). Calling it with `A0 == 0` clears the handler, matching the second call the game makes to shut VSync callbacks off.

**Files:**
- Create: `RecompOne/RecompOne.Runtime/sdk/LibApi.cs`
- Modify: `RecompOne/RecompOne.Recompiler/CodeGen/SdkPatches.cs` (add a library entry after the `LibPad` block, around line 37)
- Create: `tests/GT2Port.Tests/LibApiTests.cs`

**Interfaces:**
- Consumes: `InterruptController` from Task 1.
- Produces: `RecompOne.Runtime.Sdk.LibApi` with `VSyncCallback`, `EnterCriticalSection`, `ExitCriticalSection`, each `static void (CpuContext c, IMemory m)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/GT2Port.Tests/LibApiTests.cs`:

```csharp
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;
using Xunit;

namespace GT2Port.Tests;

[Collection("interrupts")]
public class LibApiTests
{
    public LibApiTests() => InterruptController.Reset();

    static (CpuContext, IMemory) Fixture() => (new CpuContext(), new PSMemory());

    [Fact]
    public void VSyncCallback_registers_the_handler_from_a0()
    {
        var (c, m) = Fixture();
        c.A0 = 0x80010928u;

        LibApi.VSyncCallback(c, m);

        Assert.Equal(0x80010928u, InterruptController.Handler);
    }

    [Fact]
    public void VSyncCallback_with_zero_clears_the_handler()
    {
        var (c, m) = Fixture();
        c.A0 = 0x80010928u;
        LibApi.VSyncCallback(c, m);

        c.A0 = 0u;
        LibApi.VSyncCallback(c, m);

        Assert.Equal(0u, InterruptController.Handler);
    }

    [Fact]
    public void Critical_section_calls_move_the_depth()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        Assert.Equal(1, InterruptController.CriticalDepth);

        LibApi.ExitCriticalSection(c, m);
        Assert.Equal(0, InterruptController.CriticalDepth);
    }

    [Fact]
    public void EnterCriticalSection_returns_one_in_v0()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);

        Assert.Equal(1u, c.V0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: build failure — `LibApi` does not exist.

- [ ] **Step 3: Write LibApi**

Create `RecompOne/RecompOne.Runtime/sdk/LibApi.cs`:

```csharp
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

/// <summary>
/// libapi entry points that route the game's interrupt bookkeeping into the
/// runtime's InterruptController instead of the game's own kernel tables.
/// </summary>
public static class LibApi
{
    /// <summary>VSyncCallback(func) - A0 holds the handler address, 0 clears it.</summary>
    public static void VSyncCallback(CpuContext c, IMemory m)
    {
        Log.Sdk($"VSyncCallback(0x{c.A0:X8})");
        InterruptController.SetHandler(c.A0);
        c.V0 = 0;
    }

    public static void EnterCriticalSection(CpuContext c, IMemory m)
    {
        InterruptController.EnterCritical();
        c.V0 = 1;
    }

    public static void ExitCriticalSection(CpuContext c, IMemory m)
    {
        InterruptController.ExitCritical();
        c.V0 = 0;
    }
}
```

- [ ] **Step 4: Register the library with the recompiler**

In `RecompOne/RecompOne.Recompiler/CodeGen/SdkPatches.cs`, add this entry to the `Libraries` array immediately after the `LibPad` block (before the closing `};` around line 37):

```csharp
        ("RecompOne.Runtime.Sdk.LibApi", new[]
        {
            "VSyncCallback", "EnterCriticalSection", "ExitCriticalSection",
        }),
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: PASS, 20 tests.

- [ ] **Step 6: Check the names actually match the game's symbols**

```bash
grep -oE '"(VSyncCallback|EnterCriticalSection|ExitCriticalSection|gt2_sdk_[A-Za-z]*Critical[A-Za-z]*)"' config/funcmaps/main.json | sort -u
```

The patch table matches on exact function name. The trace showed the game's exit
routine named `gt2_sdk_ExitCriticalSection`, not the bare `ExitCriticalSection`,
so at least one entry very likely needs the prefixed name. Use whatever this
command prints as the authority and correct the Step 4 entry to match.

- [ ] **Step 7: Verify the recompiler applies the new patches**

```bash
dotnet build RecompOne/RecompOne.Recompiler -c Release
dotnet run --project RecompOne/RecompOne.Recompiler -c Release --no-build -- config/gt2.json 2>&1 | grep reimplementations
```

Expected: a count higher than the current 13. If it is still 13, the names from
Step 6 were not applied — fix them before continuing, because every later task
depends on `VSyncCallback` being intercepted.

- [ ] **Step 8: Commit**

```bash
git add RecompOne/RecompOne.Runtime/sdk/LibApi.cs RecompOne/RecompOne.Recompiler/CodeGen/SdkPatches.cs tests/GT2Port.Tests/LibApiTests.cs
git commit -m "Reimplement VSyncCallback and critical sections against InterruptController"
```

---

### Task 4: Emit safepoints before backward branches

**Files:**
- Modify: `RecompOne/RecompOne.Recompiler/Config/RecompOneConfig.cs` (add `Safepoints` below `LinearSweep`, line 17)
- Modify: `RecompOne/RecompOne.Recompiler/CodeGen/InstructionEmitter.cs` (goto sites at lines 206, 221, 253, 273, 286; `FunctionContext` is declared in this file)
- Modify: `RecompOne/RecompOne.Recompiler/CodeGen/OverlayWriter.cs` (lines 247 and 318)
- Modify: `config/gt2.json`

**Interfaces:**
- Consumes: `Irq.Poll(CpuContext, IMemory)` from Task 2.
- Produces: generated code containing `Irq.Poll(c, m);` before every backward branch.

The five `goto` sites all have `target` in hand. Rather than repeating the condition five times, add one helper and route every site through it.

- [ ] **Step 1: Add the config flag**

In `RecompOne/RecompOne.Recompiler/Config/RecompOneConfig.cs`, add below the `LinearSweep` property (line 17):

```csharp
    [JsonPropertyName("safepoints")] public bool Safepoints { get; set; } = true; //emit Irq.Poll before backward branches so busy-wait loops can take interrupts
```

- [ ] **Step 2: Add the field to FunctionContext**

`FunctionContext` is declared in `RecompOne/RecompOne.Recompiler/CodeGen/InstructionEmitter.cs`. Add next to the existing `Debug` field:

```csharp
    public bool Safepoints;
```

- [ ] **Step 3: Thread the flag through OverlayWriter**

In `RecompOne/RecompOne.Recompiler/CodeGen/OverlayWriter.cs`:

Add `bool safepoints` to the `EmitOverlayFile` parameter list directly after `bool debug`. Pass `config.Safepoints` at the call site on line 247, directly after `config.Debug`. Then set the field in the `FunctionContext` initializer near line 318, next to `Debug = debug,`:

```csharp
                Safepoints = safepoints,
```

- [ ] **Step 4: Add the emit helper**

In `InstructionEmitter.cs`, add this local function next to the existing `InFunc` helper (line 199):

```csharp
        // A branch to an address at or before this instruction closes a loop.
        // That is the only place a busy-wait can take an interrupt, so it is
        // where the safepoint goes.
        void Goto(uint target, string ind)
        {
            if (ctx.Safepoints && target <= pc)
                sb.AppendLine(ctx.Trail(ctrl, $"{ind}Irq.Poll(c, m);"));
            sb.AppendLine(ctx.Trail(ctrl, $"{ind}goto L{target:X8};"));
        }
```

- [ ] **Step 5: Route the five goto sites through the helper**

- line 206 (inside `Conditional`): replace `sb.AppendLine(ctx.Trail(ctrl, $"{ind2}goto L{target:X8};"));` with `Goto(target, ind2);`
- line 221 (op 4, `rs == rt`): replace `if (InFunc(target)) sb.AppendLine(ctx.Trail(ctrl, $"{indent}goto L{target:X8};"));` with `if (InFunc(target)) Goto(target, indent);`
- line 253 (op 1, link case): replace `if (InFunc(target)) sb.AppendLine(ctx.Trail(ctrl, $"{ind2}goto L{target:X8};"));` with `if (InFunc(target)) Goto(target, ind2);`
- line 273 (op 2, `J`): replace `if (InFunc(target)) sb.AppendLine(ctx.Trail(ctrl, $"{indent}goto L{target:X8};"));` with `if (InFunc(target)) Goto(target, indent);`
- line 286 (jump table cases): the `case` label and `goto` share a line, so guard it separately:

```csharp
                foreach (uint entry in jtbl.Entries.Distinct())
                {
                    if (ctx.Safepoints && entry <= pc)
                        sb.AppendLine(ctx.Trail(ctrl, $"{indent}    case 0x{entry:X8}u: Irq.Poll(c, m); goto L{entry:X8};"));
                    else
                        sb.AppendLine(ctx.Trail(ctrl, $"{indent}    case 0x{entry:X8}u: goto L{entry:X8};"));
                }
```

- [ ] **Step 6: Add the using to generated files**

In `OverlayWriter.cs`, find the block that writes the generated file's `using` lines (it emits `using RecompOne.Runtime.Dispatch;`) and add alongside it:

```csharp
        sb.AppendLine("using RecompOne.Runtime.Interrupts;");
```

- [ ] **Step 7: Rebuild the recompiler and regenerate**

```bash
dotnet build RecompOne/RecompOne.Recompiler -c Release
dotnet run --project RecompOne/RecompOne.Recompiler -c Release --no-build -- config/gt2.json 2>&1 | tail -5
```

Expected: `Recompilation finished.`

- [ ] **Step 8: Verify a safepoint landed in the busy-wait that started all this**

```bash
awk '/void gt2_sysinit_vsync_setup\(/,/^    }$/' generated/main.cs
```

Expected: an `Irq.Poll(c, m);` line immediately before `goto L8001096C;`. If it is absent, the emission is not reaching the `Conditional` path — recheck Step 5.

```bash
grep -c "Irq.Poll" generated/main.cs
```

Expected: non-zero, and smaller than the 4445 total `goto` count — only backward branches get one.

- [ ] **Step 9: Confirm the port still builds**

```bash
dotnet build GT2Port.csproj 2>&1 | grep -E "error|êxito"
```

Expected: no errors.

- [ ] **Step 10: Commit**

```bash
git add RecompOne/RecompOne.Recompiler config/gt2.json
git commit -m "Emit Irq.Poll safepoints before backward branches"
```

---

### Task 5: Wire it together and prove the boot advances

The pieces exist but nothing starts the VBlank source or connects `Irq`'s hooks to the real dispatcher. This task closes that and proves the original defect is fixed.

**Files:**
- Modify: `Program.cs`
- Modify: `tests/GT2Port.Tests/GT2Port.Tests.csproj` (reference the port)
- Create: `tests/GT2Port.Tests/BootIntegrationTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: a port whose boot passes `gt2_sysinit`.

- [ ] **Step 1: Reference the port from the test project**

In `tests/GT2Port.Tests/GT2Port.Tests.csproj`, add to the `ItemGroup` holding the existing `ProjectReference`:

```xml
        <ProjectReference Include="../../GT2Port.csproj" />
```

`GT2Port.csproj` is an `Exe`; referencing it from a test project is supported and gives the tests access to the `Recompiled` namespace.

- [ ] **Step 2: Write the failing integration test**

Create `tests/GT2Port.Tests/BootIntegrationTests.cs`:

```csharp
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Drives the real recompiled gt2_sysinit_vsync_setup, whose busy-wait on the
/// vblank counter at 0x80011DF4 is what hung the port. Nothing here is mocked
/// except the passage of time.
/// </summary>
[Collection("interrupts")]
public class BootIntegrationTests
{
    const uint VBlankCounter = 0x80011DF4u;

    public BootIntegrationTests()
    {
        InterruptController.Reset();
        Irq.Reset();
    }

    [Fact]
    public void Vsync_setup_completes_once_vblanks_are_delivered()
    {
        var m = new PSMemory();
        var c = new CpuContext { SP = 0x801FFF00u, GP = 0u };
        c.FP = c.SP;

        Dispatcher.Register("main", new Recompiled.MainDispatchTable());
        Dispatcher.Load("main");

        Irq.Invoke = (ctx, mem, addr) => Dispatcher.Call(ctx, mem, addr);
        Irq.PumpHost = () => { };

        var source = new TestVBlankSource();
        source.Start(InterruptController.Raise);

        var done = Task.Run(() => Recompiled.GranTurismo2.gt2_sysinit_vsync_setup(c, m));

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)),
            "gt2_sysinit_vsync_setup did not return; the busy-wait never saw the counter advance");
        Assert.True((int)m.ReadU32(VBlankCounter) >= 4,
            $"vblank counter reached {(int)m.ReadU32(VBlankCounter)}, expected at least 4");

        source.Stop();
    }

    sealed class TestVBlankSource : IVBlankSource
    {
        CancellationTokenSource? _cts;

        public void Start(Action raise)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    raise();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true }.Start();
        }

        public void Stop() => _cts?.Cancel();
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter Vsync_setup_completes
```

Expected: FAIL — the test times out after 10 seconds. `VSyncCallback` registers the handler, but the safepoint's `Invoke` hook is set here while the game's own `gt2_vsync_handler` needs the dispatcher, and until Step 4 nothing in the running port wires it. If it passes already, Tasks 1-4 covered it; note that and continue.

- [ ] **Step 4: Wire the hooks and start the source in Program.cs**

In `Program.cs`, add the using alongside the existing ones:

```csharp
using RecompOne.Runtime.Interrupts;
```

Then, directly after the `Dispatcher.Load("main");` line and before the `CpuContext` is created, add:

```csharp
// Interrupt delivery: the dispatcher runs the handler, the host pump keeps the
// window alive while the game sits in a busy-wait, and the wall clock decides
// when a VBlank happens.
Irq.Invoke = (ctx, mem, addr) => Dispatcher.Call(ctx, mem, addr);
Irq.PumpHost = RecompOne.Runtime.Runtime.PumpHost;

var vblank = new WallClockVBlankSource();
vblank.Start(InterruptController.Raise);
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter Vsync_setup_completes
```

Expected: PASS.

- [ ] **Step 6: Run the whole suite**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: PASS, 21 tests.

- [ ] **Step 7: Run the real port with tracing and confirm the boot advances**

```bash
python -c "import json;p='config/gt2.json';c=json.load(open(p));c['debug']=True;json.dump(c,open(p,'w'),indent=2)"
dotnet run --project RecompOne/RecompOne.Recompiler -c Release --no-build -- config/gt2.json > /dev/null 2>&1
dotnet build GT2Port.csproj 2>&1 | grep -E "error|êxito"
```

```bash
(dotnet run --project GT2Port.csproj --no-build > trace.log 2>&1 &) ; sleep 20; taskkill //F //IM GT2Port.exe
```

```bash
grep -c "gt2_vsync_handler" trace.log
tail -20 trace.log
```

Expected: `gt2_vsync_handler` appears many times, and the trace continues past `gt2_sysinit_vsync_setup` into functions that never previously ran. The previous run ended at `INTR_VB_OBJ_C4` and emitted nothing further.

Report what the window shows. A different failure past this point is progress, not regression — record it for the next plan.

- [ ] **Step 8: Turn tracing back off**

```bash
python -c "import json;p='config/gt2.json';c=json.load(open(p));c['debug']=False;json.dump(c,open(p,'w'),indent=2)"
dotnet run --project RecompOne/RecompOne.Recompiler -c Release --no-build -- config/gt2.json 2>&1 | tail -3
dotnet build GT2Port.csproj 2>&1 | grep -E "error|êxito"
```

- [ ] **Step 9: Commit**

```bash
git add Program.cs tests/GT2Port.Tests config/gt2.json
git commit -m "Start VBlank delivery at boot and cover it with an integration test"
```

---

## Notes for the implementer

**The submodule is a fork.** Commits touching files under `RecompOne/` go to our fork's `vblank-interrupts` branch and must be committed there separately from the parent repo. After each task that changes runtime or recompiler files:

```bash
git -C RecompOne add -A
git -C RecompOne commit -m "<same message>"
```

The parent repo records the submodule's commit hash, so the submodule commit must exist before the parent commit that points at it.

**A passing integration test is not a running game.** It proves the busy-wait clears. The boot will very likely stop somewhere new — probably the overlays, which are still absent from `config/gt2.json`. That is the next plan, not this one.
