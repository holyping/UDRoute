using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UDRoute.Logging;

namespace UDRoute
{
    public static class ConfigFileHelper
    {
        private static string? ResolvePath(string? configPath)
        {
            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
            {
                return configPath;
            }

            string defaultIni = "udroute.ini";
            string? resolved = ConfigParser.ResolveIniPath(defaultIni);
            if (resolved != null && File.Exists(resolved))
            {
                return resolved;
            }

            return null;
        }

        public static void AddClientRecordToIni(string? configPath, ClientRecord rec, string? rawLine = null)
        {
            string? path = ResolvePath(configPath);
            if (path == null) return;

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
                string lineToAdd;
                if (!string.IsNullOrWhiteSpace(rawLine))
                {
                    lineToAdd = rawLine.Trim();
                }
                else
                {
                    string passStr = rec.Password != null && rec.Password.Length > 0
                        ? ":_HASH256_" + Convert.ToBase64String(rec.Password)
                        : "";
                    lineToAdd = $"{rec.Port}/{(rec.IsTcp ? "tcp" : "udp")}={rec.TargetName}{passStr}@{rec.TargetServer}";
                }

                // Check if identical line already exists
                if (lines.Any(l => l.Trim().Equals(lineToAdd, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                // Find index of first section [Section]
                int firstSecIdx = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[") && t.EndsWith("]"))
                    {
                        firstSecIdx = i;
                        break;
                    }
                }

                if (firstSecIdx >= 0)
                {
                    lines.Insert(firstSecIdx, lineToAdd);
                }
                else
                {
                    lines.Add(lineToAdd);
                }

                File.WriteAllLines(path, lines, Encoding.UTF8);
                Log.Info($"[Config] Persisted C-endpoint '{lineToAdd}' to '{path}'");
            }
            catch (Exception ex)
            {
                Log.Warn($"[Config] Failed to persist C-endpoint to '{path}': {ex.Message}");
            }
        }

        public static void RemoveClientRecordFromIni(string? configPath, int port, bool? isTcp = null)
        {
            string? path = ResolvePath(configPath);
            if (path == null) return;

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
                bool modified = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[") && t.EndsWith("]"))
                    {
                        // Stop at sections; client endpoints are in the global section
                        break;
                    }

                    int eqIdx = t.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        string left = t.Substring(0, eqIdx).Trim();
                        var parts = left.Split('/');
                        if (int.TryParse(parts[0], out int p) && p == port)
                        {
                            bool tcp = parts.Length == 1 || parts[1].Equals("tcp", StringComparison.OrdinalIgnoreCase);
                            if (isTcp == null || isTcp.Value == tcp)
                            {
                                lines.RemoveAt(i);
                                i--;
                                modified = true;
                            }
                        }
                    }
                }

                if (modified)
                {
                    File.WriteAllLines(path, lines, Encoding.UTF8);
                    Log.Info($"[Config] Removed C-endpoint on port {port} from '{path}'");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[Config] Failed to remove C-endpoint on port {port} from '{path}': {ex.Message}");
            }
        }

        public static void AddServerRecordToIni(string? configPath, ServerRecord rec, string? rawLine = null)
        {
            string? path = ResolvePath(configPath);
            if (path == null) return;

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
                string cleanName = rec.Name.EndsWith("/file", StringComparison.OrdinalIgnoreCase)
                    ? rec.Name.Substring(0, rec.Name.Length - 5)
                    : rec.Name;

                // Check if section already exists
                string header = $"[{cleanName}]";
                if (lines.Any(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                lines.Add("");
                lines.Add(header);
                if (rec.IsFile)
                {
                    lines.Add($"target={rec.BaseDir};/file");
                }
                else
                {
                    lines.Add($"target={rec.TargetIp}:{rec.TargetPort}/{(rec.IsTcp ? "tcp" : "udp")}");
                }

                if (!string.IsNullOrWhiteSpace(rec.TargetServer))
                {
                    lines.Add($"server={rec.TargetServer}");
                }

                File.WriteAllLines(path, lines, Encoding.UTF8);
                Log.Info($"[Config] Persisted S-endpoint '[{cleanName}]' to '{path}'");
            }
            catch (Exception ex)
            {
                Log.Warn($"[Config] Failed to persist S-endpoint to '{path}': {ex.Message}");
            }
        }

        public static void RemoveServerRecordFromIni(string? configPath, string secName)
        {
            string? path = ResolvePath(configPath);
            if (path == null) return;

            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
                string cleanName = secName.EndsWith("/file", StringComparison.OrdinalIgnoreCase)
                    ? secName.Substring(0, secName.Length - 5)
                    : secName;

                int startIdx = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[") && t.EndsWith("]"))
                    {
                        string s = t.Substring(1, t.Length - 2).Trim();
                        if (s.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ||
                            s.Equals(secName, StringComparison.OrdinalIgnoreCase) ||
                            s.Equals(cleanName + "/file", StringComparison.OrdinalIgnoreCase))
                        {
                            startIdx = i;
                            break;
                        }
                    }
                }

                if (startIdx >= 0)
                {
                    int countToRemove = 1;
                    for (int j = startIdx + 1; j < lines.Count; j++)
                    {
                        string t = lines[j].Trim();
                        if (t.StartsWith("[") && t.EndsWith("]"))
                        {
                            break;
                        }
                        countToRemove++;
                    }

                    lines.RemoveRange(startIdx, countToRemove);
                    File.WriteAllLines(path, lines, Encoding.UTF8);
                    Log.Info($"[Config] Removed S-endpoint '[{secName}]' from '{path}'");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[Config] Failed to remove S-endpoint '{secName}' from '{path}': {ex.Message}");
            }
        }
    }
}
