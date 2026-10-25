namespace GameServer.Tests.Infrastructure;

/// <summary>
/// The start-of-window read for zero-allocation guards built on
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
///
/// <para><b>Why a plain read is not enough.</b> The counter is
/// <c>alloc_bytes + alloc_bytes_uoh - (alloc_limit - alloc_ptr)</c>
/// (<c>src/coreclr/vm/comutilnative.cpp</c>, <c>GCInterface::GetAllocatedBytesForCurrentThread</c>):
/// <c>alloc_bytes</c> is credited with the whole allocation context when it is handed out,
/// and the unused tail <c>alloc_limit - alloc_ptr</c> is subtracted back at read time. A
/// blocking GC keeps that balanced: <c>gc_heap::fix_allocation_context</c> subtracts the tail
/// from <c>alloc_bytes</c> before it zeroes the pointers (<c>gc.cpp</c>, "We need to update the
/// alloc_bytes to reflect the portion that we have not used"). The end of a <i>background</i>
/// GC's mark phase does not: <c>gc_heap::background_mark_phase</c> calls
/// <c>repair_allocation_contexts(FALSE)</c> ("We need to void alloc contexts here 'cause while
/// background_ephemeral_sweep is running we can't let the user code consume the left over
/// parts"), which runs <c>void_allocation</c>. That zeroes <c>alloc_ptr</c>/<c>alloc_limit</c> on
/// every thread and leaves <c>alloc_bytes</c> alone, so the unused tail (up to roughly one 8 KB
/// allocation quantum) is added to the counter of every thread, including a thread that
/// allocates nothing. In a full parallel run other tests allocate, background GCs happen, and a
/// guard window that spans one reads a few KB (SC-003: 96 B ... 7 KB) it never allocated.</para>
///
/// <para><b>The fix.</b> <c>void_allocation</c> and <c>fix_allocation_context</c> both return early
/// when <c>alloc_ptr == 0</c>, so a thread whose context is already empty cannot be inflated.
/// A blocking gen0 collection empties the calling thread's context the balanced way. Calling
/// it immediately before the first read starts the window with an empty context; code that
/// allocates nothing never refills it, so nothing can inflate the counter during the window.
/// A real allocation inside the window refills the context and still shows up as at least
/// its own size, which is why the assertion stays exactly zero and no threshold is raised.</para>
/// </summary>
internal static class ThreadAllocation
{
    /// <summary>
    /// Empties the calling thread's allocation context and returns the counter. Call it
    /// last, after all set-up and warm-up, with no allocating statement between it and the
    /// code under measurement.
    /// </summary>
    public static long Begin()
    {
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        return GC.GetAllocatedBytesForCurrentThread();
    }
}
