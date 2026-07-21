using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PrintServerLauncher
{
    class Program
    {
        // GitHub Repository URL details
        private const string GITHUB_USER = "alfredodelperu";
        private const string GITHUB_REPO = "ControlImpresiones";
        private const string API_URL = "https://api.github.com/repos/" + GITHUB_USER + "/" + GITHUB_REPO + "/releases/latest";
        
        // Local files
        private const string VERSION_FILE = "version.txt";
        private const string SERVER_EXE = "Server.exe";
        private const string UPDATE_ZIP = "update.zip";

        static void Main(string[] args)
        {
            // Forzar el uso de TLS 1.2 que exige GitHub, clave para .NET 4.5/4.8
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("========================================");
            Console.WriteLine("    Iniciando Control de Impresiones... ");
            Console.WriteLine("========================================");
            Console.ResetColor();

            // 0. Auto-Descubrimiento de rutas si no hay config.txt
            AutoConfigurePaths();

            // 0.5 Verificar si el puerto 8085 está abierto en el Firewall
            CheckAndOpenFirewallPort();

            // 1. Obtener version local
            string localVersion = GetLocalVersion();
            Console.WriteLine("Versión Local: " + localVersion);

            // 2. Revisar si hay actualización en GitHub
            string latestVersion = "";
            string downloadUrl = "";
            
            try
            {
                Console.WriteLine("Buscando actualizaciones en GitHub...");
                
                using (WebClient client = new WebClient())
                {
                    // GitHub API requiere un User-Agent valido
                    client.Headers.Add("User-Agent", "PrintServer-Updater");
                    
                    string json = client.DownloadString(API_URL);
                    
                    // Extraer tag_name usando Regex (ej: "tag_name": "v1.1.0")
                    Match mTag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (mTag.Success)
                    {
                        latestVersion = mTag.Groups[1].Value;
                    }

                    // Extraer browser_download_url
                    Match mUrl = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
                    if (mUrl.Success)
                    {
                        downloadUrl = mUrl.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No se pudo contactar al servidor de actualizaciones (Sin Internet o Límite de API).");
                Console.ResetColor();
            }

            // 3. Comparar e Instalar si es necesario
            if (!string.IsNullOrEmpty(latestVersion) && !string.IsNullOrEmpty(downloadUrl) && localVersion != latestVersion)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("¡Nueva versión encontrada!: " + latestVersion);
                Console.ResetColor();
                Console.WriteLine("Descargando actualización, por favor espere...");

                try
                {
                    // Descargar ZIP
                    using (WebClient client = new WebClient())
                    {
                        client.Headers.Add("User-Agent", "PrintServer-Updater");
                        client.DownloadFile(downloadUrl, UPDATE_ZIP);
                    }

                    Console.WriteLine("Instalando actualización...");
                    
                    // Asegurarse de que el Servidor viejo esté cerrado antes de sobreescribir
                    Process[] runningServers = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SERVER_EXE));
                    foreach (Process p in runningServers)
                    {
                        p.Kill();
                        p.WaitForExit();
                    }

                    // Extraer ZIP sobreescribiendo archivos viejos
                    using (ZipArchive archive = ZipFile.OpenRead(UPDATE_ZIP))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string destinationPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, entry.FullName));

                            // Directorios
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(destinationPath);
                                continue;
                            }

                            // Evitar sobreescribir config.txt si ya existe para no borrar las rutas del cliente
                            if (entry.Name.ToLower() == "config.txt" && File.Exists(destinationPath))
                            {
                                continue;
                            }

                            // Archivos ordinarios
                            entry.ExtractToFile(destinationPath, true);
                        }
                    }

                    // Actualizar archivito local de version
                    File.WriteAllText(VERSION_FILE, latestVersion);
                    
                    // Limpieza
                    if (File.Exists(UPDATE_ZIP)) File.Delete(UPDATE_ZIP);

                    Console.WriteLine("¡Actualización instalada con éxito!");
                    Thread.Sleep(1000); // Pequeña pausa visual
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error crítico durante la actualización: " + ex.Message);
                    Console.ResetColor();
                    Console.WriteLine("Continuando con la versión local...");
                    Thread.Sleep(2000);
                }
            }
            else if (!string.IsNullOrEmpty(latestVersion) && localVersion == latestVersion)
            {
                Console.WriteLine("El sistema está actualizado.");
            }

            // 4. Iniciar el Servidor Real
            if (File.Exists(SERVER_EXE))
            {
                Console.WriteLine("Iniciando " + SERVER_EXE + "...");
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = SERVER_EXE;
                Process.Start(startInfo);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: No se encontró " + SERVER_EXE + ". El programa no puede arrancar.");
                Console.ResetColor();
                Console.WriteLine("Presiona Enter para salir...");
                Console.ReadLine();
            }
        }

        static string GetLocalVersion()
        {
            if (File.Exists(VERSION_FILE))
            {
                return File.ReadAllText(VERSION_FILE).Trim();
            }
            
            // Si no existe, creamos uno con base v1.0.0
            string baseVer = "v1.0.0";
            File.WriteAllText(VERSION_FILE, baseVer);
            return baseVer;
        }

        static void AutoConfigurePaths()
        {
            string configPath = "config.txt";
            if (File.Exists(configPath)) return;

            Console.WriteLine("Generando config.txt automáticamente...");
            
            string[] searchRip = { @"C:\Program Files\SAi", @"C:\Program Files (x86)\SAi" };
            
            string[] searchExp = { 
                @"C:\Program Files (x86)\PrintExp_X64", 
                @"C:\Program Files\PrintExp",
                @"C:\Program Files (x86)\PrintExp",
                @"C:\Program Files\Printext",
                @"C:\Program Files (x86)\Printext"
            };

            string foundRipPath = @"C:\";
            string foundTfPath = @"C:\";
            string foundTxtPath = @"C:\";

            // Buscar RIPLOG: Nivel 1 (Registro de Windows para FlexiPrint/SAi)
            string registryRip = GetRegistryInstallPath("Flexi"); // Busca algo como FlexiPrint
            if (string.IsNullOrEmpty(registryRip)) registryRip = GetRegistryInstallPath("SAi");
            
            if (!string.IsNullOrEmpty(registryRip) && Directory.Exists(registryRip))
            {
                Console.WriteLine("Buscando RIPLOG en Directorio del Registro (" + registryRip + ")...");
                string result = SafeSearchFile(registryRip, "RIPLOG.HTML");
                if (string.IsNullOrEmpty(result)) result = SafeSearchFile(registryRip, "RIPLOG.html");
                if (!string.IsNullOrEmpty(result)) {
                    foundRipPath = result;
                    Console.WriteLine("-> RIPLOG encontrado vía Registro en: " + foundRipPath);
                }
            }

            // Buscar RIPLOG: Nivel 2 (Rutas Comunes)
            if (foundRipPath == @"C:\")
            {
                foreach (string pdir in searchRip)
                {
                    if (Directory.Exists(pdir))
                    {
                        Console.WriteLine("Buscando RIPLOG en instalación estándar (" + pdir + ")...");
                        
                        // Optimización Heurística Instantánea (Basado en arquitectura conocida FlexiPrint)
                        try {
                            foreach (string sub in Directory.GetDirectories(pdir)) {
                                string targetDir = Path.Combine(sub, "Jobs and Settings");
                                if (File.Exists(Path.Combine(targetDir, "RIPLOG.HTML"))) {
                                    foundRipPath = targetDir + "\\";
                                    break;
                                }
                                if (File.Exists(Path.Combine(targetDir, "RIPLOG.html"))) {
                                    foundRipPath = targetDir + "\\";
                                    break;
                                }
                            }
                        } catch {}

                        if (foundRipPath != @"C:\") {
                            Console.WriteLine("-> RIPLOG encontrado vía estructura FlexiPrint en: " + foundRipPath);
                            break;
                        }

                        // Escaneo Recursivo Local
                        string result = SafeSearchFile(pdir, "RIPLOG.HTML");
                        if (string.IsNullOrEmpty(result)) result = SafeSearchFile(pdir, "RIPLOG.html"); // Case sensitivity fallback
                        
                        if (!string.IsNullOrEmpty(result)) {
                            foundRipPath = result;
                            Console.WriteLine("-> RIPLOG encontrado en escaneo local: " + foundRipPath);
                            break;
                        }
                    }
                }
            }

            // Buscar RIPLOG: Nivel 3 (Escaneo Profundo de Disco Duro)
            if (foundRipPath == @"C:\")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("RIPLOG no encontrado. Iniciando escaneo profundo del Disco C:\\... (Esto puede tomar unos minutos)");
                Console.ResetColor();
                string result = SafeSearchFile(@"C:\", "RIPLOG.HTML");
                if (string.IsNullOrEmpty(result)) result = SafeSearchFile(@"C:\", "RIPLOG.html");
                if (!string.IsNullOrEmpty(result)) {
                    foundRipPath = result;
                    Console.WriteLine("-> RIPLOG encontrado en escaneo profundo en: " + foundRipPath);
                }
            }

            // Buscar PrintExp / Printext
            foreach (string pdir in searchExp)
            {
                if (Directory.Exists(pdir))
                {
                    string dataDir = Path.Combine(pdir, "Data");
                    string logDir = Path.Combine(pdir, "Log");
                    
                    if (Directory.Exists(dataDir)) {
                        foundTfPath = dataDir + "\\";
                        Console.WriteLine("-> PrintExp TF encontrado en: " + foundTfPath);
                    }
                    if (Directory.Exists(logDir)) {
                        foundTxtPath = logDir + "\\";
                        Console.WriteLine("-> PrintExp TXT encontrado en: " + foundTxtPath);
                    }
                    if (Directory.Exists(dataDir)) break;
                }
            }

            try
            {
                File.WriteAllLines(configPath, new string[] { foundRipPath, foundTfPath, foundTxtPath });
                Console.WriteLine("Archivo config.txt autogenerado exitosamente.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("No se pudo autogenerar config.txt: " + ex.Message);
            }
        }

        static string SafeSearchFile(string root, string filename)
        {
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                string curr = queue.Dequeue();
                try
                {
                    string[] files = Directory.GetFiles(curr, filename);
                    if (files.Length > 0) return Path.GetDirectoryName(files[0]) + "\\";
                    
                    foreach (string d in Directory.GetDirectories(curr))
                    {
                        queue.Enqueue(d);
                    }
                }
                catch { } // Ignorar subcarpetas protegidas por el SO
            }
            return "";
        }

        static string GetRegistryInstallPath(string appName)
        {
            try
            {
                string[] keys = new string[] {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (string keyPath in keys)
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyPath))
                    {
                        if (key != null)
                        {
                            foreach (string subkeyName in key.GetSubKeyNames())
                            {
                                using (RegistryKey subkey = key.OpenSubKey(subkeyName))
                                {
                                    if (subkey != null)
                                    {
                                        object displayName = subkey.GetValue("DisplayName");
                                        if (displayName != null && displayName.ToString().IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            object installLocation = subkey.GetValue("InstallLocation");
                                            if (installLocation != null && !string.IsNullOrEmpty(installLocation.ToString()))
                                            {
                                                return installLocation.ToString();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            } catch { }
            return "";
        }

        static void CheckAndOpenFirewallPort()
        {
            try
            {
                // Verify if rule exists silently
                ProcessStartInfo checkInfo = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"Control Impresiones Server\"");
                checkInfo.CreateNoWindow = true;
                checkInfo.UseShellExecute = false;
                checkInfo.RedirectStandardOutput = true;
                using (Process proc = Process.Start(checkInfo))
                {
                    proc.WaitForExit();
                    // Si ExitCode no es 0, la regla no existe
                    if (proc.ExitCode != 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("Configurando Firewall y Red Local por primera vez...");
                        Console.ResetColor();
                        
                        // Agregar regla pidiendo permisos UAC de Administrador de forma limpia
                        // Permite puerto TCP 8085 en Firewall Y Autoriza el Binding Universal HTTP (urlacl)
                        string firewallCmd = "netsh advfirewall firewall add rule name=\"Control Impresiones Server\" dir=in action=allow protocol=TCP localport=8085";
                        string urlaclCmd = "netsh http add urlacl url=http://+:8085/ sddl=D:(A;;GX;;;WD)";
                        
                        ProcessStartInfo addInfo = new ProcessStartInfo("cmd.exe", "/c " + firewallCmd + " && " + urlaclCmd);
                        addInfo.Verb = "runas";
                        addInfo.CreateNoWindow = true;
                        addInfo.WindowStyle = ProcessWindowStyle.Hidden;
                        
                        using (Process addProc = Process.Start(addInfo))
                        {
                            addProc.WaitForExit();
                        }
                        Console.WriteLine("Red y Firewall configurados con éxito. El puerto 8085 está abierto (LAN).");
                        Thread.Sleep(1500);
                    }
                }
            }
            catch (Exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Advertencia: No se pudo configurar el Firewall automáticamente o se denegaron los permisos.");
                Console.ResetColor();
            }
        }
    }
}
