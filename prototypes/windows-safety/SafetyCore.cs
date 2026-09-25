using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DevHarbor.Probe {
    public sealed class BoundaryLease : IDisposable {
        readonly List<SafeFileHandle> handles = new List<SafeFileHandle>();
        public string Fingerprint { get; private set; }
        public string FinalPath { get; private set; }
        [StructLayout(LayoutKind.Sequential)] struct Info {
            public uint Attr; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out Info info);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder path, uint size, uint flags);
        static Exception Error() { return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        static string Canonical(string path) {
            if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\") || path.Length < 3 || path[1] != ':' || path[2] != '\\') throw new InvalidOperationException("Only absolute local drive paths are supported");
            if (path.Substring(2).Contains(":")) throw new InvalidOperationException("Alternate streams rejected");
            foreach (var part in path.Substring(3).Split('\\','/'))
                if (part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")) throw new InvalidOperationException("Ambiguous path rejected");
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        public static BoundaryLease Open(string root, string target) {
            var lease = new BoundaryLease();
            try {
                root = Canonical(root); target = Canonical(target);
                if (!target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Outside root or root itself");
                string volume = Path.GetPathRoot(target);
                if (new DriveInfo(volume).DriveType != DriveType.Fixed) throw new InvalidOperationException("Only fixed local drives supported");
                string[] parts = target.Substring(volume.Length).Split('\\');
                string current = volume;
                // Ancestors cannot be renamed while held; leaf cannot be written or renamed.
                for (int i=-1; i<parts.Length; i++) {
                    if (i>=0) current=Path.Combine(current,parts[i]);
                    bool leaf = i == parts.Length-1;
                    var h=CreateFileW(current,leaf ? 0x80000000u : 0x80u,leaf ? 1u : 3u,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
                    if(h.IsInvalid) { var error=Error(); h.Dispose(); throw new IOException("Cannot open boundary component: "+current+"; "+error.Message,error); }
                    lease.handles.Add(h);
                    Info info; if(!GetFileInformationByHandle(h,out info)) throw Error();
                    if((info.Attr & (0x400u | 0x1000u | 0x40000u | 0x400000u))!=0) throw new InvalidOperationException("Reparse/offline/recall path rejected");
                    if(leaf) {
                        if((info.Attr & 0x10)!=0 || info.Links != 1) throw new InvalidOperationException("Only single-link regular files supported");
                        var b=new StringBuilder(32768);
                        uint n=GetFinalPathNameByHandleW(h,b,(uint)b.Capacity,0);
                        if(n==0 || n>=b.Capacity) throw Error();
                        lease.FinalPath=b.ToString();
                        if(!String.Equals(lease.FinalPath,@"\\?\"+target,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Resolved path changed");
                        lease.Fingerprint=Hash(String.Join("|",new object[]{lease.FinalPath,info.Volume,info.IndexHigh,info.IndexLow,info.SizeHigh,info.SizeLow,info.Written.dwHighDateTime,info.Written.dwLowDateTime}));
                    }
                }
                return lease;
            } catch { lease.Dispose(); throw; }
        }
        public static string Hash(string text) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-",""); }
        public void Dispose() { for(int i=handles.Count-1;i>=0;i--) handles[i].Dispose(); handles.Clear(); }
    }

    // In-memory state-machine experiment, NOT a production authorization service.
    public sealed class ApprovalProbe {
        readonly string root, target, fingerprint, digest;
        readonly DateTime expires;
        readonly object sync=new object();
        string state="AwaitingApproval";
        public string State { get { lock(sync) return state; } }
        public string Digest { get { return digest; } }
        public ApprovalProbe(string root,string target,DateTime now) {
            this.root=root; this.target=target; expires=now.AddMinutes(3);
            using(var lease=BoundaryLease.Open(root,target)) fingerprint=lease.Fingerprint;
            digest=BoundaryLease.Hash(Guid.NewGuid()+"|"+target+"|probe-only|"+fingerprint+"|"+expires.Ticks);
        }
        public void DecideFromTestUI(string shownDigest,bool approved,DateTime now) {
            lock(sync) {
                if(state!="AwaitingApproval") throw new InvalidOperationException("Not awaiting approval");
                if(now>=expires) { state="Expired"; throw new InvalidOperationException("Expired"); }
                if(shownDigest!=digest) throw new InvalidOperationException("Plan mismatch");
                state=approved?"Approved":"Denied";
            }
        }
        public void ExecuteProbe(string suppliedDigest,DateTime now,Action harmlessAction) {
            lock(sync) {
                if(now>=expires) { state="Expired"; throw new InvalidOperationException("Expired"); }
                if(state!="Approved" || suppliedDigest!=digest) throw new InvalidOperationException("No matching approval");
                state="Revalidating";
                try {
                    using(var lease=BoundaryLease.Open(root,target)) {
                        if(lease.Fingerprint!=fingerprint) throw new InvalidOperationException("Target changed");
                        state="Executing"; harmlessAction(); state="Completed";
                    }
                } catch { state="Failed"; throw; }
            }
        }
    }
}
