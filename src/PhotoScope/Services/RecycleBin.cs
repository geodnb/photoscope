using System.Runtime.InteropServices;

namespace PhotoScope.Services;

public enum RecycleOutcome
{
    /// <summary>The file is now in the Recycle Bin.</summary>
    Recycled,
    /// <summary>Windows couldn't recycle it, so nothing was done. The file is untouched.</summary>
    Refused,
    /// <summary>The operation failed (file in use, access denied...). The file is still in place.</summary>
    Failed,
    /// <summary>
    /// Windows removed the file without creating a Recycle Bin item. The safeguards exist to make this
    /// impossible; if it ever happens it is reported plainly rather than as success.
    /// </summary>
    PermanentlyDeleted,
}

public sealed record RecycleResult(RecycleOutcome Outcome, string? Message = null);

/// <summary>
/// Sends files to the Recycle Bin with the Windows IFileOperation API, configured so that it can
/// never delete permanently:
/// <list type="bullet">
/// <item>FOF_ALLOWUNDO + FOFX_RECYCLEONDELETE ask for recycling;</item>
/// <item>FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI mean Windows shows no dialogs at all, so
/// there is never a "permanently delete?" prompt anyone could click Yes on;</item>
/// <item>a progress sink checks every item in PreDeleteItem and aborts the whole operation unless
/// Windows says it will recycle that item (TSF_DELETE_RECYCLE_IF_POSSIBLE). An aborted item is
/// left exactly where it was.</item>
/// </list>
/// The sink is a backstop, not the first line of defence: Microsoft documents the flag as controlling
/// the delete but doesn't formally promise it is cleared in every case where the shell would delete
/// permanently. <see cref="FileOps.CanRecycle"/> therefore runs first and refuses every known
/// no-bin situation, and the outcome is classified from what actually happened to the file.
/// Must be called on an STA thread.
/// </summary>
public static class RecycleBin
{
    public static RecycleResult Recycle(string path) => Recycle(path, simulateNotRecyclable: false);

    /// <param name="simulateNotRecyclable">Tests only: the sink treats the item as not recyclable.</param>
    internal static RecycleResult Recycle(string path, bool simulateNotRecyclable)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("RecycleBin.Recycle must run on an STA thread.");
        if (!File.Exists(path)) return new RecycleResult(RecycleOutcome.Failed, "The file could not be found.");

        IFileOperation? op = null;
        IShellItem? item = null;
        var sink = new Sink(simulateNotRecyclable);
        uint cookie = 0;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidFileOperation, throwOnError: true)!;
            op = (IFileOperation)Activator.CreateInstance(type)!;
            op.SetOperationFlags(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofNoConfirmMkdir
                                 | FofxRecycleOnDelete | FofxEarlyFailure);
            cookie = op.Advise(sink);
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, ref iid, out item);
            op.DeleteItem(item, null);
            var hr = op.PerformOperations();
            var aborted = false;
            try { aborted = op.GetAnyOperationsAborted(); } catch { }
            var stillThere = File.Exists(path);

            // Classify by what happened to the file, using the sink's per-item reports first.
            if (sink.Refused && stillThere)
            {
                Log.Info($"Recycle refused by Windows (would not go to the Recycle Bin): {path}");
                return new RecycleResult(RecycleOutcome.Refused, "Windows couldn't put this file in the Recycle Bin.");
            }
            if (sink.Recycled && !stillThere)
            {
                if (hr < 0) Log.Warn($"Recycled {path} but the operation reported 0x{hr:X8}");
                return new RecycleResult(RecycleOutcome.Recycled);
            }
            if (!stillThere && (sink.DeletedWithoutRecycle || !sink.PreDeleteCalled || !sink.Recycled))
            {
                Log.Error($"PERMANENT DELETE: {path} is gone but Windows reported no Recycle Bin item " +
                          $"(pre-delete check ran: {sink.PreDeleteCalled}, hr=0x{hr:X8}, delete hr=0x{sink.DeleteResult:X8})");
                return new RecycleResult(RecycleOutcome.PermanentlyDeleted,
                    "Windows deleted this file without putting it in the Recycle Bin. PhotoScope's safeguards should have prevented this; details were written to the log.");
            }
            Log.Warn($"Recycle failed for {path}: hr=0x{hr:X8}, delete hr=0x{sink.DeleteResult:X8}, aborted={aborted}");
            var detail = sink.DeleteResult < 0 ? sink.DeleteResult : hr;
            var reason = detail < 0 ? Friendly.Describe(Marshal.GetExceptionForHR(detail) ?? new IOException()) : "Windows didn't move the file.";
            return new RecycleResult(RecycleOutcome.Failed, reason);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Recycle could not start for {path}", ex);
            return new RecycleResult(RecycleOutcome.Failed, Friendly.Describe(ex));
        }
        finally
        {
            if (op != null && cookie != 0) { try { op.Unadvise(cookie); } catch { } }
            if (item != null) Marshal.ReleaseComObject(item);
            if (op != null) Marshal.ReleaseComObject(op);
        }
    }

    // ------------------------------------------------------------------ progress sink

    [ComVisible(true)]
    sealed class Sink(bool simulateNotRecyclable) : IFileOperationProgressSink
    {
        public bool Refused;
        public bool PreDeleteCalled;
        public bool Recycled;
        public bool DeletedWithoutRecycle;
        public int DeleteResult;

        public int PreDeleteItem(uint flags, IShellItem item)
        {
            PreDeleteCalled = true;
            // Windows sets TSF_DELETE_RECYCLE_IF_POSSIBLE only when this item will go to the bin.
            // Anything else would be a permanent delete: abort the whole operation.
            if (simulateNotRecyclable || (flags & TsfDeleteRecycleIfPossible) == 0)
            {
                Refused = true;
                return EAbort;
            }
            return 0;
        }

        public int PostDeleteItem(uint flags, IShellItem item, int hrDelete, IShellItem? newlyCreated)
        {
            DeleteResult = hrDelete;
            if (hrDelete >= 0)
            {
                if (newlyCreated != null) Recycled = true; // the new item is the file inside the Recycle Bin
                else DeletedWithoutRecycle = true;
            }
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int hrResult) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string? newName) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string? newName, int hr, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? newName, int hr, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? newName, int hr, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string? newName) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string? newName, string? templateName, uint attributes, int hr, IShellItem? created) => 0;
        public int UpdateProgress(uint workTotal, uint workSoFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    // ------------------------------------------------------------------ interop

    const uint FofSilent = 0x0004;
    const uint FofNoConfirmation = 0x0010;
    const uint FofAllowUndo = 0x0040;
    const uint FofNoConfirmMkdir = 0x0200;
    const uint FofNoErrorUi = 0x0400;
    const uint FofxRecycleOnDelete = 0x00080000;
    const uint FofxEarlyFailure = 0x00100000;
    const uint TsfDeleteRecycleIfPossible = 0x0080;
    const int EAbort = unchecked((int)0x80004004);

    static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hrResult);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int hr, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int hr, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int hr, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int hrDelete, IShellItem? newlyCreated);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint attributes, int hr, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint workTotal, uint workSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink sink);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object dialog);
        void SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IFileOperationProgressSink? sink);
        void RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
        void MoveItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
        void CopyItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }
}
