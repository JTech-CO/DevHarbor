using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DevHarbor.Probe {
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem {
        void BindToHandler(IntPtr pbc,ref Guid bhid,ref Guid iid,out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind,out IntPtr name);
        void GetAttributes(uint mask,out uint attrs);
        void Compare(IShellItem other,uint hint,out int order);
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation {
        void Advise(IProgress sink,out uint cookie); void Unadvise(uint cookie);
        void SetOperationFlags(uint flags); void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(uint hwnd);
        void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item,[MarshalAs(UnmanagedType.LPWStr)]string name,IProgress sink); void RenameItems(IntPtr items,[MarshalAs(UnmanagedType.LPWStr)]string name);
        void MoveItem(IShellItem item,IShellItem destination,[MarshalAs(UnmanagedType.LPWStr)]string name,IProgress sink); void MoveItems(IntPtr items,IShellItem destination);
        void CopyItem(IShellItem item,IShellItem destination,[MarshalAs(UnmanagedType.LPWStr)]string name,IProgress sink); void CopyItems(IntPtr items,IShellItem destination);
        void DeleteItem(IShellItem item,IProgress sink); void DeleteItems(IntPtr items);
        void NewItem(IShellItem destination,uint attrs,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string template,IProgress sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)]out bool aborted);
    }
    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IProgress {
        [PreserveSig]int StartOperations(); [PreserveSig]int FinishOperations(int result);
        [PreserveSig]int PreRenameItem(uint flags,IShellItem item,[MarshalAs(UnmanagedType.LPWStr)]string name);
        [PreserveSig]int PostRenameItem(uint flags,IShellItem item,[MarshalAs(UnmanagedType.LPWStr)]string name,int result,IShellItem created);
        [PreserveSig]int PreMoveItem(uint flags,IShellItem item,IShellItem dest,[MarshalAs(UnmanagedType.LPWStr)]string name);
        [PreserveSig]int PostMoveItem(uint flags,IShellItem item,IShellItem dest,[MarshalAs(UnmanagedType.LPWStr)]string name,int result,IShellItem created);
        [PreserveSig]int PreCopyItem(uint flags,IShellItem item,IShellItem dest,[MarshalAs(UnmanagedType.LPWStr)]string name);
        [PreserveSig]int PostCopyItem(uint flags,IShellItem item,IShellItem dest,[MarshalAs(UnmanagedType.LPWStr)]string name,int result,IShellItem created);
        [PreserveSig]int PreDeleteItem(uint flags,IShellItem item);
        [PreserveSig]int PostDeleteItem(uint flags,IShellItem item,int result,IShellItem created);
        [PreserveSig]int PreNewItem(uint flags,IShellItem dest,[MarshalAs(UnmanagedType.LPWStr)]string name);
        [PreserveSig]int PostNewItem(uint flags,IShellItem dest,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string template,uint attrs,int result,IShellItem created);
        [PreserveSig]int UpdateProgress(uint total,uint done); [PreserveSig]int ResetTimer(); [PreserveSig]int PauseTimer(); [PreserveSig]int ResumeTimer();
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class Sink : IProgress {
        public string RecycledPath; public bool Completed; public int Result; public bool RejectDelete;
        public int StartOperations(){return 0;} public int FinishOperations(int r){return 0;}
        public int PreRenameItem(uint f,IShellItem i,string n){return 0;} public int PostRenameItem(uint f,IShellItem i,string n,int r,IShellItem c){return 0;}
        public int PreMoveItem(uint f,IShellItem i,IShellItem d,string n){return 0;}
        public int PostMoveItem(uint f,IShellItem i,IShellItem d,string n,int r,IShellItem c){Completed=true;Result=r;return 0;}
        public int PreCopyItem(uint f,IShellItem i,IShellItem d,string n){return unchecked((int)0x80004004);}
        public int PostCopyItem(uint f,IShellItem i,IShellItem d,string n,int r,IShellItem c){return 0;}
        public int PreDeleteItem(uint f,IShellItem i){return RejectDelete || (f & 0x80)==0 ? unchecked((int)0x80004004) : 0;}
        public int PostDeleteItem(uint f,IShellItem i,int r,IShellItem c){Completed=true;Result=r;if(c!=null)RecycledPath=RecycleProbe.Name(c);return 0;}
        public int PreNewItem(uint f,IShellItem d,string n){return unchecked((int)0x80004004);}
        public int PostNewItem(uint f,IShellItem d,string n,string t,uint a,int r,IShellItem c){return 0;}
        public int UpdateProgress(uint t,uint d){return 0;} public int ResetTimer(){return 0;} public int PauseTimer(){return 0;} public int ResumeTimer(){return 0;}
    }
    public static class RecycleProbe {
        [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]
        static extern void SHCreateItemFromParsingName(string path,IntPtr bind,ref Guid iid,out IShellItem item);
        static IShellItem Item(string path){Guid iid=typeof(IShellItem).GUID;IShellItem item;SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out item);return item;}
        public static string Name(IShellItem item){IntPtr p;item.GetDisplayName(0x80028000,out p);try{return Marshal.PtrToStringUni(p);}finally{Marshal.FreeCoTaskMem(p);}}
        static IFileOperation Operation(){return (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));}
        public static string RecycleSynthetic(string path,bool veto){
            // This experiment intentionally cannot accept arbitrary names or payloads.
            if(!Path.GetFileName(path).StartsWith("devharbor-fixture-") || !File.ReadAllText(path).StartsWith("DEVHARBOR SYNTHETIC FIXTURE\n")) throw new InvalidOperationException("Not a synthetic fixture");
            var op=Operation(); var item=Item(path);var sink=new Sink();sink.RejectDelete=veto;
            try{
                op.SetOperationFlags(0x00080000|0x00004000|0x00000400|0x00100000|0x00000004);
                op.DeleteItem(item,sink);op.PerformOperations();bool aborted;op.GetAnyOperationsAborted(out aborted);
                if(aborted || !sink.Completed || sink.Result<0) throw new InvalidOperationException("Recycle rejected or incomplete: "+sink.Result);
                if(String.IsNullOrEmpty(sink.RecycledPath))throw new InvalidOperationException("No durable recycle reference");
                return sink.RecycledPath;
            }finally{Marshal.ReleaseComObject(item);Marshal.ReleaseComObject(op);}
        }
        public static void RestoreSynthetic(string recycled,string destination){
            if(!Path.GetFileName(destination).StartsWith("devharbor-fixture-") || File.Exists(destination) || Directory.Exists(destination))throw new InvalidOperationException("Restore collision or non-fixture name");
            if(recycled.IndexOf("$Recycle.Bin",StringComparison.OrdinalIgnoreCase)<0 || !File.ReadAllText(recycled).StartsWith("DEVHARBOR SYNTHETIC FIXTURE\n"))throw new InvalidOperationException("Not this experiment's recycled payload");
            var op=Operation();var item=Item(recycled);var dest=Item(Path.GetDirectoryName(destination));var sink=new Sink();
            try{op.SetOperationFlags(0x400|0x100000|0x4);op.MoveItem(item,dest,Path.GetFileName(destination),sink);op.PerformOperations();bool aborted;op.GetAnyOperationsAborted(out aborted);if(aborted || !sink.Completed || sink.Result<0)throw new InvalidOperationException("Restore incomplete");}
            finally{Marshal.ReleaseComObject(item);Marshal.ReleaseComObject(dest);Marshal.ReleaseComObject(op);}
        }
    }
}
