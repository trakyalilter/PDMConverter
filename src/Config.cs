using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace PdmApproveConvert
{
    internal sealed class Rule
    {
        public HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string TaskName;
    }

    /// <summary>
    /// Settings. Defaults are safe: DryRun = true, so nothing is started until
    /// an admin creates %ProgramData%\PdmApproveConvert\config.xml with DryRun="false".
    /// The file is re-read on every state change, so edits apply without restarting PDM.
    /// </summary>
    internal sealed class Config
    {
        public HashSet<string> ApprovedStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<Rule> Rules = new List<Rule>();
        // States a CHILD file may be in to be converted along with an approved assembly.
        // Separate from ApprovedStates because they must not trigger anything by themselves.
        public HashSet<string> ChildStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool IncludeAssemblyChildren = true;
        public bool ChildrenMustBeApproved = true;
        public bool DryRun = true;

        // Pilot lock: when set, only files whose name starts with this are ever touched
        // (top-level or child). Empty = no restriction.
        public string NamePrefix = "";

        public bool NameAllowed(string fileName)
        {
            return NamePrefix.Length == 0 ||
                   (fileName != null && fileName.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase));
        }

        public static string ConfigPath
        {
            get
            {
                // Override for testing: set PDMAPPROVECONVERT_CONFIG to another xml file.
                string over = Environment.GetEnvironmentVariable("PDMAPPROVECONVERT_CONFIG");
                if (!string.IsNullOrEmpty(over)) return over;

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "PdmApproveConvert", "config.xml");
            }
        }

        private static bool _warnedNoConfig;

        /// <summary>
        /// Returns null when there is no config file: the add-in is then completely inert on this PC.
        /// That is deliberate, because the add-in is installed vault-wide and must only act where an
        /// admin has put a config.xml.
        /// </summary>
        public static Config Load()
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                if (!_warnedNoConfig)
                {
                    _warnedNoConfig = true;
                    Log.Write("inert: no config file at " + path);
                }
                return null;
            }
            Config c = Defaults();

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (root == null) return c;

                XmlNodeList states = root.SelectNodes("ApprovedStates/State");
                if (states != null && states.Count > 0)
                {
                    c.ApprovedStates.Clear();
                    foreach (XmlNode n in states)
                        if (!string.IsNullOrWhiteSpace(n.InnerText)) c.ApprovedStates.Add(n.InnerText.Trim());
                }

                XmlNodeList childStates = root.SelectNodes("ChildStates/State");
                if (childStates != null && childStates.Count > 0)
                {
                    c.ChildStates.Clear();
                    foreach (XmlNode n in childStates)
                        if (!string.IsNullOrWhiteSpace(n.InnerText)) c.ChildStates.Add(n.InnerText.Trim());
                }

                XmlNodeList rules = root.SelectNodes("Rules/Rule");
                if (rules != null && rules.Count > 0)
                {
                    c.Rules.Clear();
                    foreach (XmlNode n in rules)
                    {
                        XmlElement e = (XmlElement)n;
                        string task = e.GetAttribute("Task").Trim();
                        if (task.Length == 0) continue;
                        Rule r = new Rule { TaskName = task };
                        foreach (string ext in e.GetAttribute("Extensions").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            r.Extensions.Add(ext.StartsWith(".") ? ext : "." + ext);
                        if (r.Extensions.Count > 0) c.Rules.Add(r);
                    }
                }

                c.IncludeAssemblyChildren = ReadBool(root, "IncludeAssemblyChildren", c.IncludeAssemblyChildren);
                c.ChildrenMustBeApproved = ReadBool(root, "ChildrenMustBeApproved", c.ChildrenMustBeApproved);
                c.DryRun = ReadBool(root, "DryRun", c.DryRun);

                XmlNode prefix = root.SelectSingleNode("OnlyNamesStartingWith");
                if (prefix != null) c.NamePrefix = prefix.InnerText.Trim();
            }
            catch (Exception ex)
            {
                // Bad config: stay on safe defaults (DryRun) instead of guessing.
                Log.Error("config load (" + path + ") - using defaults, DryRun=true", ex);
                return Defaults();
            }
            return c;
        }

        private static Config Defaults()
        {
            Config c = new Config();
            // Names taken from the CNC vault (Probe.exe): one "manager approved" state per workflow.
            c.ApprovedStates.Add("Manager Approved");          // SW Workflow
            c.ApprovedStates.Add("Library-Manager Approved");  // LIBRARY
            c.ApprovedStates.Add("SWE Managers Approved");     // SWE Workflow

            // Children: approved states, plus the "sent to ERP" states parts move on to.
            foreach (string s in c.ApprovedStates) c.ChildStates.Add(s);
            c.ChildStates.Add("Send To Erp-Only PART!");
            c.ChildStates.Add("Send Library Part to Erp-Only PART!");
            c.ChildStates.Add("FILES SENT TO ERP");

            Rule step = new Rule { TaskName = "STEP file from 3D Model" };
            step.Extensions.Add(".sldprt");
            step.Extensions.Add(".sldasm");
            c.Rules.Add(step);

            Rule pdf = new Rule { TaskName = "PDF file from 2D Drawing" };
            pdf.Extensions.Add(".slddrw");
            c.Rules.Add(pdf);
            return c;
        }

        private static bool ReadBool(XmlElement root, string name, bool fallback)
        {
            XmlNode n = root.SelectSingleNode(name);
            if (n == null) return fallback;
            bool b;
            return bool.TryParse(n.InnerText.Trim(), out b) ? b : fallback;
        }
    }
}
