using System;
using System.Threading;
using Xunit;
using RecompOne.Runtime.Dispatch;

namespace GT2Port.Tests;

/// <summary>
/// The task scheduler's one promise: exactly one task runs at a time.
///
/// Cooperative tasks share a single register file - one CpuContext for the
/// whole game - so two threads running at once do not merely interleave, they
/// overwrite each other's registers. That surfaces far from the cause, as a
/// pointer that has become nonsense midway through a hot loop, which is why
/// the promise is worth asserting directly rather than inferring from a crash.
/// </summary>
public class TaskStacksTests
{
    /// <summary>A stack pointer low enough to count as a task's own stack.</summary>
    const uint TaskSp = 0x800E2A0Cu;

    /// <summary>Long enough that a hand-off gone wrong shows up as a hang, not a pass.</summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    static void Max(ref int slot, int value)
    {
        int seen = Volatile.Read(ref slot);
        while (value > seen)
        {
            int was = Interlocked.CompareExchange(ref slot, value, seen);
            if (was == seen) return;
            seen = was;
        }
    }

    /// <summary>Runs <paramref name="body"/> on a thread of its own so a deadlock fails rather than hangs.</summary>
    static void WithinPatience(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.Start();

        Assert.True(thread.Join(Patience), "the scheduler never handed the baton back");
        if (failure is not null) throw failure;
    }

    [Fact]
    public void OnlyOneTaskRunsAtATime()
    {
        int live = 0, peak = 0;

        // The measured stretch must not hand the baton over. A task parked
        // partway through a nested call is still on the stack but is not
        // running, so counting frames in flight would call correct nesting a
        // race. Counting only stretches that never yield asks the real
        // question: were two threads ever executing at the same moment?
        void Alone()
        {
            Max(ref peak, Interlocked.Increment(ref live));
            Thread.Sleep(1);          // widen the window a second runner would need
            Interlocked.Decrement(ref live);
        }

        void Busy(Action? nested)
        {
            Alone();
            nested?.Invoke();
            Alone();
        }

        WithinPatience(() =>
        {
            for (int i = 0; i < 200; i++)
                TaskStacks.Start(
                    TaskStacks.RegionOf(TaskSp),
                    () => Busy(() => TaskStacks.Start(0, () => Busy(null))));
        });

        Assert.Equal(1, peak);
    }

    /// <summary>
    /// A task asked to run something while it is parked must still hand the
    /// baton back to the task that parked it, not to the one that interrupted.
    ///
    /// Main starts A, A starts B, and B calls back into A. That last call
    /// arrives while A is parked waiting on B, so A now owes the baton to two
    /// different tasks at two different depths. Getting the pairing wrong sends
    /// A's final hand-off to B instead of to main, and main waits for a baton
    /// that has already gone somewhere else.
    /// </summary>
    [Fact]
    public void AnInterruptedTaskStillAnswersWhoeverParkedIt()
    {
        const int A = 0x800E, B = 0x800F;
        int reached = 0;

        WithinPatience(() =>
            TaskStacks.Start(A, () =>
                TaskStacks.Start(B, () =>
                    TaskStacks.Start(A, () => Interlocked.Increment(ref reached)))));

        Assert.Equal(1, reached);
    }

    [Fact]
    public void EveryPieceOfWorkRuns()
    {
        int ran = 0;

        WithinPatience(() =>
        {
            for (int i = 0; i < 200; i++)
                TaskStacks.Start(TaskStacks.RegionOf(TaskSp), () =>
                {
                    Interlocked.Increment(ref ran);
                    TaskStacks.Start(0, () => Interlocked.Increment(ref ran));
                });
        });

        Assert.Equal(400, ran);
    }
}
