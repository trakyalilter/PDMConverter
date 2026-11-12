using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EdmLib;

namespace PdmApproveConvert
{
    /// <summary>
    /// SOLIDWORKS PDM add-in. After a file changes state (EdmCmd_PostState) and lands in an
    /// "approved" state, it starts the configured PDM Convert tasks for that file and,
    /// for assemblies, for the referenced child files.
    ///
    /// It only QUEUES tasks (IEdmTaskMgr.RunTask). The PDM Task Host does the real
    /// conversion, exactly as if a user picked Tasks > Convert in Explorer.
    /// </summary>
    [ComVisible(true)]
    [Guid("6F0B3A52-8C1D-4E7B-9A64-2D5E7C1B8F30")]
    [ProgId("PdmApproveConvert.AddIn")]
    [ClassInterface(ClassInterfaceType.None)]
    public class ApproveConvertAddIn : IEdmAddIn5
    {
        private const int MaxDepth = 64;

        public void GetAddInInfo(ref EdmAddInInfo poInfo, IEdmVault5 poVault, IEdmCmdMgr5 poCmdMgr)
        {
            try
            {
                poInfo.mbsAddInName = "Approve to Convert";
                poInfo.mbsCompany = "CNC";
                poInfo.mbsDescription = "Starts PDM Convert tasks (PDF/STEP) when a file is approved.";
                poInfo.mlAddInVersion = 1;
                poInfo.mlRequiredVersionMajor = 6;
                poInfo.mlRequiredVersionMinor = 4;

                poCmdMgr.AddHook(EdmCmdType.EdmCmd_PostState, null);
                Log.Write("GetAddInInfo: hooked EdmCmd_PostState");
            }
            catch (Exception ex)
            {
                Log.Error("GetAddInInfo", ex);
            }
        }

        public void OnCmd(ref EdmCmd poCmd, ref EdmCmdData[] ppoData)
        {
            // A PDM add-in must never throw into the host. Swallow and log everything.
            try
            {
                if (poCmd.meCmdType == EdmCmdType.EdmCmd_PostState)
                    HandlePostState(poCmd, ppoData);
            }
            catch (Exception ex)
            {
                Log.Error("OnCmd", ex);
            }
        }

        private sealed class Target
        {
            public int FileId;
            public int FolderId;
            public bool IsTopLevel; // the file whose state the user changed
        }

        private static void HandlePostState(EdmCmd cmd, EdmCmdData[] data)
        {
            if (data == null || data.Length == 0) return;

            Config cfg = Config.Load();
            if (cfg == null) return; // no config.xml on this PC: do nothing at all

            IEdmVault7 vault = (IEdmVault7)cmd.mpoVault;

            // Candidate files, de-duplicated by file id (a part can be both approved and a child).
            Dictionary<int, Target> targets = new Dictionary<int, Target>();

            foreach (EdmCmdData d in data)
            {
                // Raw dump: lets you confirm the field layout on the first real run.
                Log.Write(string.Format("PostState raw: id1={0} id2={1} s1='{2}' s2='{3}' s3='{4}'",
                    d.mlObjectID1, d.mlObjectID2, d.mbsStrData1, d.mbsStrData2, d.mbsStrData3));

                IEdmFile5 file = TryGetFile(vault, d.mlObjectID1);
                if (file == null) continue; // folder state change, or gone

                if (!cfg.NameAllowed(file.Name))
                {
                    Log.Write("skip '" + file.Name + "': name does not start with '" + cfg.NamePrefix + "'");
                    continue;
                }

                string state = StateName(file);
                if (!cfg.ApprovedStates.Contains(state))
                {
                    Log.Write("skip '" + file.Name + "': state '" + state + "' is not an approved state");
                    continue;
                }

                int folderId = d.mlObjectID2 > 0 ? d.mlObjectID2 : FirstFolderId(file);
                AddTarget(targets, file.ID, folderId, true);

                if (cfg.IncludeAssemblyChildren &&
                    string.Equals(Path.GetExtension(file.Name), ".sldasm", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        CollectChildren(file, folderId, targets);
                    }
                    catch (Exception ex)
                    {
                        // Keep what we have; still convert the assembly itself.
                        Log.Error("reference walk of '" + file.Name + "'", ex);
                    }
                }
            }

            if (targets.Count == 0) return;

            // Group selections by task name according to the rules.
            Dictionary<string, List<EdmSelItem2>> byTask =
                new Dictionary<string, List<EdmSelItem2>>(StringComparer.OrdinalIgnoreCase);

            foreach (Target t in targets.Values)
            {
                IEdmFile5 f = TryGetFile(vault, t.FileId);
                if (f == null) continue;
                if (!cfg.NameAllowed(f.Name)) continue;

                if (!t.IsTopLevel && cfg.ChildrenMustBeApproved && !cfg.ChildStates.Contains(StateName(f)))
                {
                    Log.Write("skip child '" + f.Name + "': state '" + StateName(f) + "' is not in ChildStates");
                    continue;
                }

                string ext = Path.GetExtension(f.Name);
                foreach (Rule r in cfg.Rules)
                {
                    if (!r.Extensions.Contains(ext)) continue;

                    List<EdmSelItem2> list;
                    if (!byTask.TryGetValue(r.TaskName, out list))
                    {
                        list = new List<EdmSelItem2>();
                        byTask[r.TaskName] = list;
                    }

                    EdmSelItem2 item = new EdmSelItem2();
                    item.mlID = f.ID;
                    item.meType = EdmObjectType.EdmObject_File;
                    item.mlParentID = t.FolderId;
                    item.mlVersion = f.CurrentVersion;
                    list.Add(item);
                    Log.Write("queue '" + r.TaskName + "' <- " + f.Name + " (v" + f.CurrentVersion + ")");
                }
            }

            if (byTask.Count == 0)
            {
                Log.Write("no rule matched any approved file");
                return;
            }

            // Never use IEdmTaskMgr.GetTasks() directly (heap corruption) - see TaskApi.
            List<TaskApi.TaskEntry> tasks = TaskApi.GetTasks(vault, false);

            foreach (KeyValuePair<string, List<EdmSelItem2>> kv in byTask)
            {
                TaskApi.TaskEntry? info = FindTask(tasks, kv.Key);
                if (info == null)
                {
                    // Task may have been created after the cache was filled: one refresh, then give up.
                    tasks = TaskApi.GetTasks(vault, true);
                    info = FindTask(tasks, kv.Key);
                }
                if (info == null)
                {
                    Log.Write("TASK NOT FOUND: '" + kv.Key + "'. Available: " + TaskNames(tasks));
                    continue;
                }

                if (cfg.DryRun)
                {
                    Log.Write("DRY RUN: would run task '" + kv.Key + "' on " + kv.Value.Count + " file(s)");
                    continue;
                }

                try
                {
                    // Written BEFORE the call: if PDM dies inside RunTask, this line is the evidence.
                    Log.Write("calling RunTask '" + kv.Key + "' on " + kv.Value.Count + " file(s), parentWnd=" + cmd.mlParentWnd);
                    TaskApi.Run(vault, info.Value, kv.Value.ToArray(), cmd.mlParentWnd);
                    Log.Write("STARTED task '" + kv.Key + "' on " + kv.Value.Count + " file(s)");
                }
                catch (Exception ex)
                {
                    Log.Error("RunTask '" + kv.Key + "'", ex);
                }
            }
        }

        private static void CollectChildren(IEdmFile5 top, int folderId, Dictionary<int, Target> targets)
        {
            IEdmReference5 root = top.GetReferenceTree(folderId, 0); // 0 = latest version
            if (root == null) return;
            Walk(root, true, targets, 0);
        }

        private static void Walk(IEdmReference5 parent, bool isTop, Dictionary<int, Target> targets, int depth)
        {
            if (depth > MaxDepth) return;

            string project = "";
            IEdmPos5 pos = parent.GetFirstChildPosition(ref project, isTop, true, 0);
            while (!pos.IsNull)
            {
                IEdmReference5 child = parent.GetNextChild(pos);
                if (child == null) continue;

                if (!targets.ContainsKey(child.FileID))
                {
                    AddTarget(targets, child.FileID, child.FolderID, false);
                    Walk(child, false, targets, depth + 1);
                }
            }
        }

        private static void AddTarget(Dictionary<int, Target> targets, int fileId, int folderId, bool top)
        {
            Target t;
            if (targets.TryGetValue(fileId, out t))
            {
                if (top) t.IsTopLevel = true;
                return;
            }
            targets[fileId] = new Target { FileId = fileId, FolderId = folderId, IsTopLevel = top };
        }

        private static IEdmFile5 TryGetFile(IEdmVault7 vault, int id)
        {
            try
            {
                IEdmFile5 f = vault.GetObject(EdmObjectType.EdmObject_File, id) as IEdmFile5;
                if (f != null && f.ObjectType == EdmObjectType.EdmObject_File) return f;
            }
            catch
            {
                // id is not a file
            }
            return null;
        }

        private static string StateName(IEdmFile5 f)
        {
            try
            {
                IEdmState5 s = f.CurrentState;
                return s == null ? "" : s.Name;
            }
            catch
            {
                return "";
            }
        }

        private static int FirstFolderId(IEdmFile5 f)
        {
            IEdmPos5 pos = f.GetFirstFolderPosition();
            if (pos.IsNull) return 0;
            IEdmFolder5 folder = f.GetNextFolder(pos);
            return folder == null ? 0 : folder.ID;
        }

        private static TaskApi.TaskEntry? FindTask(List<TaskApi.TaskEntry> tasks, string name)
        {
            if (tasks == null) return null;
            string wanted = NormalizeName(name);
            foreach (TaskApi.TaskEntry t in tasks)
                if (string.Equals(NormalizeName(t.Name), wanted, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        // PDM task names may contain double spaces ("PDF  file from 2D Drawing"); ignore that when matching.
        private static string NormalizeName(string s)
        {
            return s == null ? "" : System.Text.RegularExpressions.Regex.Replace(s.Trim(), "\\s+", " ");
        }

        private static string TaskNames(List<TaskApi.TaskEntry> tasks)
        {
            if (tasks == null || tasks.Count == 0) return "(none)";
            List<string> n = new List<string>();
            foreach (TaskApi.TaskEntry t in tasks) n.Add("'" + t.Name + "'");
            return string.Join(", ", n.ToArray());
        }
    }
}
