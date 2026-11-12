using System;
using System.Collections.Generic;
using EdmLib;

// Probe: read-only look at a PDM vault (tasks, workflows, states) so config.xml uses the
// exact names. Optional --run starts ONE task on ONE file to test headless launch.
//
//   Probe.exe CNC
//   Probe.exe CNC --file "C:\PDMView\CNC\Folder\Part1.SLDPRT"
//   Probe.exe CNC --run "Convert PDF" "C:\PDMView\CNC\Folder\Drawing1.SLDDRW"
internal static class Probe
{
    private static void RunStatesChild(string vaultName)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                System.Reflection.Assembly.GetExecutingAssembly().Location, "\"" + vaultName + "\" --states-child");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                Console.Write(output); // exit code ignored on purpose, see above
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  (could not list states: " + ex.Message + ")");
        }
    }

    private static void StatesChild(string vaultName)
    {
        IEdmVault7 vault = (IEdmVault7)new EdmVault5();
        vault.LoginAuto(vaultName, 0);
        object wu = vault.CreateUtility(EdmUtility.EdmUtil_WorkflowMgr);
        IEdmWorkflowMgr6 wm = (IEdmWorkflowMgr6)wu;
        IEdmPos5 wp = wm.GetFirstWorkflowPosition();
        while (!wp.IsNull)
        {
            IEdmWorkflow6 wf = wm.GetNextWorkflow(wp);
            Console.WriteLine("  Workflow '" + wf.Name + "'");
            IEdmPos5 sp = wf.GetFirstStatePosition();
            while (!sp.IsNull)
            {
                IEdmState6 st = wf.GetNextState(sp);
                Console.WriteLine("      state '" + st.Name + "'");
            }
        }
        Console.Out.Flush();
        // Skip normal shutdown on a possibly corrupted heap.
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[1] == "--states-child")
        {
            StatesChild(args[0]);
            return 0;
        }

        if (args.Length < 1)
        {
            Console.WriteLine("usage: Probe <vaultName> [--file <path>] [--run <taskName> <filePath>]");
            return 2;
        }

        try
        {
            IEdmVault7 vault = (IEdmVault7)new EdmVault5();
            vault.LoginAuto(args[0], 0);
            int major = 0, minor = 0;
            vault.GetVersion(ref major, ref minor);
            Console.WriteLine("Vault '" + vault.Name + "' logged in. API version " + major + "." + minor);

            // IEdmTaskMgr.GetTasks() directly corrupts the heap; TaskApi reads it safely.
            List<PdmApproveConvert.TaskApi.TaskEntry> tasks = PdmApproveConvert.TaskApi.GetTasks(vault, false);

            Console.WriteLine();
            Console.WriteLine("TASKS (use these exact names in config.xml):");
            foreach (PdmApproveConvert.TaskApi.TaskEntry t in tasks)
                Console.WriteLine("  '" + t.Name + "'  id=" + t.Id + "  flags=0x" + t.Flags.ToString("X"));

            Console.WriteLine();
            Console.WriteLine("WORKFLOWS / STATES (use these exact state names in <ApprovedStates>):");
            // IEdmWorkflow6.GetNextState corrupts the heap (0xC0000374 on later COM calls), so it runs
            // in a throw-away child process. The add-in itself never enumerates states.
            RunStatesChild(args[0]);

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--file" && i + 1 < args.Length)
                {
                    ShowFile(vault, args[++i]);
                }
                else if (args[i] == "--stress" && i + 1 < args.Length)
                {
                    int n = int.Parse(args[++i]);
                    for (int k = 0; k < n; k++)
                    {
                        List<PdmApproveConvert.TaskApi.TaskEntry> x = PdmApproveConvert.TaskApi.GetTasks(vault, true);
                        if (x.Count != tasks.Count) Console.WriteLine("count changed " + x.Count);
                        if (k % 50 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); Console.WriteLine("call " + k + " ok"); }
                    }
                    Console.WriteLine("stress done");
                }
                else if (args[i] == "--find" && i + 2 < args.Length)
                {
                    Find(vault, args[i + 1], args[i + 2]);
                    i += 2;
                }
                else if (args[i] == "--simulate" && i + 1 < args.Length)
                {
                    Simulate(vault, args[++i]);
                }
                else if (args[i] == "--run" && i + 2 < args.Length)
                {
                    RunOne(vault, tasks, args[i + 1], args[i + 2]);
                    i += 2;
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex.Message);
            return 1;
        }
    }

    private static void ShowFile(IEdmVault7 vault, string path)
    {
        IEdmFolder5 folder;
        IEdmFile5 f = vault.GetFileFromPath(path, out folder);
        Console.WriteLine();
        Console.WriteLine("FILE " + path);
        Console.WriteLine("  id=" + f.ID + " folderId=" + folder.ID + " version=" + f.CurrentVersion +
                          " state='" + (f.CurrentState == null ? "" : f.CurrentState.Name) + "'");

        IEdmReference5 root = f.GetReferenceTree(folder.ID, 0);
        Console.WriteLine("  references:");
        Dump(root, true, 2, new HashSet<int>());
    }

    private static void Dump(IEdmReference5 parent, bool top, int indent, HashSet<int> seen)
    {
        if (indent > 40) return;
        string proj = "";
        IEdmPos5 pos = parent.GetFirstChildPosition(ref proj, top, true, 0);
        while (!pos.IsNull)
        {
            IEdmReference5 c = parent.GetNextChild(pos);
            IEdmFile5 cf = c.File;
            string st = "";
            try { st = cf.CurrentState == null ? "" : cf.CurrentState.Name; } catch { }
            Console.WriteLine(new string(' ', indent * 2) + c.Name + "  [state '" + st + "']");
            if (seen.Add(c.FileID)) Dump(c, false, indent + 1, seen);
        }
    }

    // Lists up to 10 files matching a name pattern in a given state, e.g. --find "*.sldasm" "Manager Approved"
    private static void Find(IEdmVault7 vault, string pattern, string state)
    {
        IEdmSearch5 s = vault.CreateSearch();
        s.FileName = pattern;
        object stateArg = state;
        s.set_State(ref stateArg);
        s.FindFiles = true;
        s.Recursive = true;
        s.StartFolderID = vault.RootFolderID;
        Console.WriteLine();
        Console.WriteLine("FIND '" + pattern + "' in state '" + state + "':");
        IEdmSearchResult5 r = s.GetFirstResult();
        int n = 0;
        while (r != null && n < 10)
        {
            Console.WriteLine("  " + r.Path + "   [" + r.StateName + "]");
            n++;
            r = s.GetNextResult();
        }
        if (n == 0) Console.WriteLine("  (no results)");
    }

    // Feeds the add-in a fake "PostState" for one file, exactly as PDM would. With DryRun=true in the
    // config (default), it only logs: %LOCALAPPDATA%\PdmApproveConvert\addin.log
    private static void Simulate(IEdmVault7 vault, string path)
    {
        IEdmFolder5 folder;
        IEdmFile5 f = vault.GetFileFromPath(path, out folder);

        EdmCmd cmd = new EdmCmd();
        cmd.meCmdType = EdmCmdType.EdmCmd_PostState;
        cmd.mpoVault = vault;

        EdmCmdData d = new EdmCmdData();
        d.mlObjectID1 = f.ID;
        d.mlObjectID2 = folder.ID;
        EdmCmdData[] data = new[] { d };

        Console.WriteLine();
        Console.WriteLine("SIMULATE PostState for " + path + " (state '" + f.CurrentState.Name + "')");
        new PdmApproveConvert.ApproveConvertAddIn().OnCmd(ref cmd, ref data);
        Console.WriteLine("Done. See log: " + System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdmApproveConvert", "addin.log"));
    }

    private static void RunOne(IEdmVault7 vault, List<PdmApproveConvert.TaskApi.TaskEntry> tasks, string taskName, string path)
    {
        IEdmFolder5 folder;
        IEdmFile5 f = vault.GetFileFromPath(path, out folder);
        foreach (PdmApproveConvert.TaskApi.TaskEntry t in tasks)
        {
            if (!string.Equals(t.Name, taskName, StringComparison.OrdinalIgnoreCase)) continue;
            EdmSelItem2 item = new EdmSelItem2();
            item.mlID = f.ID;
            item.meType = EdmObjectType.EdmObject_File;
            item.mlParentID = folder.ID;
            item.mlVersion = f.CurrentVersion;
            PdmApproveConvert.TaskApi.Run(vault, t, new[] { item }, 0);
            Console.WriteLine();
            Console.WriteLine("RunTask '" + t.Name + "' started on " + path + ". Check Task status in PDM.");
            return;
        }
        Console.WriteLine("Task '" + taskName + "' not found.");
    }
}
