using System;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Web;
using System.Data.SQLite;
using System.Drawing;

namespace RipLogViewer
{
    class Program
    {
        static void Main(string[] args)
        {
            string ripLogDir = @"C:\Program Files\SAi\FlexiPRINT 22 WitColor Edition\Jobs and Settings";
            string printExpDir = @"C:\Program Files (x86)\PrintExp_X64\Data";
            string logTxtDir = @"C:\Program Files (x86)\PrintExp_X64\Data";

            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            if (File.Exists(configPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configPath, Encoding.UTF8);
                    if (lines.Length >= 1 && !string.IsNullOrWhiteSpace(lines[0])) ripLogDir = lines[0].Trim();
                    if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1])) printExpDir = lines[1].Trim();
                    if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2])) logTxtDir = lines[2].Trim();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error leyendo config.txt: " + ex.Message);
                }
            }
            else
            {
                try
                {
                    File.WriteAllLines(configPath, new string[] { ripLogDir, printExpDir, logTxtDir }, Encoding.UTF8);
                    Console.WriteLine("Archivo config.txt creado con las rutas por defecto.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("No se pudo crear config.txt: " + ex.Message);
                }
            }

            string ripLogPath = Path.Combine(ripLogDir, "RIPLOG.HTML");
            string printExpPath = Path.Combine(printExpDir, "HistoryTask.tf");
            
            string port = "8085";
            string bindUrl = "http://+:" + port + "/";
            string localUrl = "http://localhost:" + port + "/";

            // Inicializar DB
            DatabaseManager.InitializeDatabase();
            
            // Iniciar Background Thread para Sincronización DB
            Thread syncThread = new Thread(() => DatabaseManager.SyncLoop(ripLogDir, printExpDir, logTxtDir));
            syncThread.IsBackground = true;
            syncThread.Start();

            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(bindUrl);
            try
            {
                listener.Start();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("========================================");
                Console.WriteLine("    CONTROL DE IMPRESIONES INICIADO");
                Console.WriteLine("========================================");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("Servidor web escuchando en toda la Red Local (LAN).");
                Console.WriteLine("Carpeta RIPLOG: " + ripLogDir);
                Console.WriteLine("Carpeta TF: " + printExpDir);
                Console.WriteLine();
                Console.WriteLine("Abre tu navegador local en: " + localUrl);
                Console.WriteLine("O desde otra PC en la red usando tu IP: http://TU_IP:" + port + "/");
                Console.WriteLine("Presiona Ctrl+C para detener el servidor.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error al iniciar el servidor:");
                Console.WriteLine(ex.Message);
                Console.ResetColor();
                Console.ReadLine();
                return;
            }

            while (true)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(state => 
                    {
                        try {
                            ProcessRequest(context, ripLogPath, printExpPath, logTxtDir);
                        } catch (Exception ex) {
                            Console.WriteLine("Error en hilo de solicitud: " + ex.Message);
                        }
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error al aceptar solicitud: " + e.Message);
                }
            }
        }

        static void ProcessRequest(HttpListenerContext context, string ripLogPath, string printExpPath, string logTxtDir)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            response.AppendHeader("Access-Control-Allow-Origin", "*");

            string path = request.Url.AbsolutePath.ToLower();

            // Refresh paths from config.txt dynamically on every request
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            if (File.Exists(configPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configPath, Encoding.UTF8);
                    if (lines.Length >= 1 && !string.IsNullOrWhiteSpace(lines[0])) ripLogPath = Path.Combine(lines[0].Trim(), "RIPLOG.HTML");
                    if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1])) printExpPath = Path.Combine(lines[1].Trim(), "HistoryTask.tf");
                    if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2])) logTxtDir = lines[2].Trim();
                }
                catch { }
            }

            try
            {
                if (path == "/api/rip")
                {
                    string ripHtml = "";
                    string ripLogDir = Path.GetDirectoryName(ripLogPath);
                    string altRipLogPath = Path.Combine(ripLogDir, "RIPLOG.html");
                    if (File.Exists(ripLogPath)) {
                        using (var fs = new FileStream(ripLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs, Encoding.Default)) {
                            ripHtml = reader.ReadToEnd();
                        }
                    } else if (File.Exists(altRipLogPath)) {
                        using (var fs = new FileStream(altRipLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs, Encoding.Default)) {
                            ripHtml = reader.ReadToEnd();
                        }
                    }
                    string machineName = Environment.MachineName;
                    string json = string.Format("{{\"machineName\":\"{0}\", \"logHtml\":\"{1}\"}}", 
                        HttpUtility.JavaScriptStringEncode(machineName), 
                        HttpUtility.JavaScriptStringEncode(ripHtml));

                    SendJsonResponse(response, json);
                }
                else if (path.StartsWith("/api/sql_riplog"))
                {
                    string dateStart = request.QueryString["date_start"];
                    string dateEnd = request.QueryString["date_end"];
                    string filenameParam = request.QueryString["filename"];
                    string eventoParam = request.QueryString["evento"];
                    string machineParam = request.QueryString["machine"];
                    string anchoMin = request.QueryString["ancho_min"];
                    string anchoMax = request.QueryString["ancho_max"];
                    string largoMin = request.QueryString["largo_min"];
                    string largoMax = request.QueryString["largo_max"];

                    string query = "SELECT * FROM riplog WHERE 1=1";
                    
                    if (!string.IsNullOrWhiteSpace(dateStart)) query += " AND StartTime >= @dateStart";
                    if (!string.IsNullOrWhiteSpace(dateEnd)) query += " AND StartTime <= @dateEnd";
                    if (!string.IsNullOrWhiteSpace(filenameParam)) query += " AND FileName LIKE @filename";
                    if (!string.IsNullOrWhiteSpace(eventoParam)) query += " AND State = @evento";
                    if (!string.IsNullOrWhiteSpace(anchoMin)) query += " AND Width >= @anchoMin";
                    if (!string.IsNullOrWhiteSpace(anchoMax)) query += " AND Width <= @anchoMax";
                    if (!string.IsNullOrWhiteSpace(largoMin)) query += " AND Length >= @largoMin";
                    if (!string.IsNullOrWhiteSpace(largoMax)) query += " AND Length <= @largoMax";

                    query += " ORDER BY StartTime DESC LIMIT 1000";

                    var jsonList = new List<string>();
                    string machineName = Environment.MachineName;

                    bool returnEmpty = (!string.IsNullOrWhiteSpace(machineParam) && !machineParam.Equals(machineName, StringComparison.OrdinalIgnoreCase));

                    if (!returnEmpty) {
                        try {
                            using (var conn = new SQLiteConnection(DatabaseManager.connectionString))
                            {
                                conn.Open();
                                using (var cmd = new SQLiteCommand(query, conn))
                                {
                                    if (!string.IsNullOrWhiteSpace(dateStart)) cmd.Parameters.AddWithValue("@dateStart", dateStart + " 00:00:00");
                                    if (!string.IsNullOrWhiteSpace(dateEnd)) cmd.Parameters.AddWithValue("@dateEnd", dateEnd + " 23:59:59");
                                    if (!string.IsNullOrWhiteSpace(filenameParam)) cmd.Parameters.AddWithValue("@filename", "%" + filenameParam + "%");
                                    if (!string.IsNullOrWhiteSpace(eventoParam)) cmd.Parameters.AddWithValue("@evento", eventoParam);
                                    if (!string.IsNullOrWhiteSpace(anchoMin)) cmd.Parameters.AddWithValue("@anchoMin", Convert.ToDouble(anchoMin, CultureInfo.InvariantCulture));
                                    if (!string.IsNullOrWhiteSpace(anchoMax)) cmd.Parameters.AddWithValue("@anchoMax", Convert.ToDouble(anchoMax, CultureInfo.InvariantCulture));
                                    if (!string.IsNullOrWhiteSpace(largoMin)) cmd.Parameters.AddWithValue("@largoMin", Convert.ToDouble(largoMin, CultureInfo.InvariantCulture));
                                    if (!string.IsNullOrWhiteSpace(largoMax)) cmd.Parameters.AddWithValue("@largoMax", Convert.ToDouble(largoMax, CultureInfo.InvariantCulture));

                                    using (var reader = cmd.ExecuteReader())
                                    {
                                        while (reader.Read())
                                        {
                                            DateTime sTime;
                                            string formattedTime = "";
                                            if (DateTime.TryParse(reader["StartTime"].ToString(), out sTime)) {
                                                formattedTime = sTime.ToString("yyyy/MM/dd HH:mm:ss");
                                            } else {
                                                formattedTime = reader["StartTime"].ToString();
                                            }

                                            int copiasVal = 1;
                                            try { if (reader["Copias"] != DBNull.Value) copiasVal = Convert.ToInt32(reader["Copias"]); } catch {}

                                            jsonList.Add(string.Format(CultureInfo.InvariantCulture,
                                                "{{\"fileName\":\"{0}\",\"state\":\"{1}\",\"startTime\":\"{2}\",\"width\":{3},\"length\":{4},\"copias\":{5},\"machineName\":\"{6}\"}}",
                                                HttpUtility.JavaScriptStringEncode(reader["FileName"].ToString()),
                                                HttpUtility.JavaScriptStringEncode(reader["State"].ToString()),
                                                formattedTime,
                                                reader["Width"],
                                                reader["Length"],
                                                copiasVal,
                                                HttpUtility.JavaScriptStringEncode(machineName)
                                            ));
                                        }
                                    }
                                }
                            }
                        } catch (Exception ex) { Console.WriteLine("API RIPLOG SQL Err: " + ex.Message); }
                    }

                    string printedJson = "[" + string.Join(",", jsonList.ToArray()) + "]";
                    string json = string.Format("{{\"machineName\":\"{0}\", \"logJobs\":{1}}}", 
                        HttpUtility.JavaScriptStringEncode(machineName), printedJson);

                    SendJsonResponse(response, json);
                }
                else if (path.StartsWith("/api/print_details"))
                {
                    string filename = request.QueryString["filename"];
                    string starttime = request.QueryString["starttime"];
                    string machineName = Environment.MachineName;

                    string jsonResult = "{}";

                    if (!string.IsNullOrWhiteSpace(filename))
                    {
                        try
                        {
                            using (var conn = new SQLiteConnection(DatabaseManager.connectionString))
                            {
                                conn.Open();
                                
                                string cleanName = filename.Trim();
                                string datePrefix = !string.IsNullOrWhiteSpace(starttime) && starttime.Length >= 10 ? starttime.Substring(0, 10).Replace("-", "/").Replace("/", "-") : "";

                                string safeName = cleanName.Replace("'", "''");

                                // 1. RIPLOG
                                string ripFileName = cleanName, ripState = "PRINT", ripStartTime = starttime ?? "";
                                double ripWidth = 0, ripLength = 0; int ripCopias = 1;

                                string debugLog = "";
                                try {
                                    using (var cmd = new SQLiteCommand("SELECT * FROM riplog WHERE FileName LIKE '%" + safeName + "%' ORDER BY StartTime DESC LIMIT 1", conn))
                                    {
                                        using (var r = cmd.ExecuteReader())
                                        {
                                            if (r.Read())
                                            {
                                                ripFileName = r["FileName"].ToString();
                                                ripState = r["State"].ToString();
                                                ripStartTime = r["StartTime"].ToString();
                                                double.TryParse(r["Width"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out ripWidth);
                                                double.TryParse(r["Length"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out ripLength);
                                                try { if (r["Copias"] != DBNull.Value) ripCopias = Convert.ToInt32(r["Copias"]); } catch {}
                                            }
                                        }
                                    }
                                    debugLog += "[RipLog OK] ";
                                } catch (Exception ex1) { debugLog += "[RipLog Err: " + ex1.Message + "] "; }

                                // 2. TXT Log
                                bool hasTxt = false;
                                string txtStart = "", txtEnd = "", txtMode = "";
                                double txtW = 0, txtL = 0, txtProd = 0;
                                int txtCopies = 1, txtCompleted = 0, txtTPass = 0, txtMPass = 0;

                                try
                                {
                                    using (var cmd = new SQLiteCommand("SELECT * FROM logtxt WHERE JobName LIKE '%" + safeName + "%' ORDER BY StartTime DESC LIMIT 1", conn))
                                    {
                                        using (var r = cmd.ExecuteReader())
                                        {
                                            if (r.Read())
                                            {
                                                hasTxt = true;
                                                txtStart = r["StartTime"].ToString();
                                                txtEnd = r["EndTime"].ToString();
                                                txtMode = r["Mode"].ToString();
                                                double.TryParse(r["Width"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out txtW);
                                                double.TryParse(r["Length"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out txtL);
                                                double.TryParse(r["ProductionRatio"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out txtProd);
                                                try { txtCopies = Convert.ToInt32(r["Copies"]); } catch {}
                                                try { txtCompleted = Convert.ToInt32(r["Completed"]); } catch {}
                                                try { txtTPass = Convert.ToInt32(r["TotalPass"]); } catch {}
                                                try { txtMPass = Convert.ToInt32(r["MaxPass"]); } catch {}
                                            }
                                        }
                                    }
                                    debugLog += "[TxtLog OK] ";
                                }
                                catch (Exception ex2) { debugLog += "[TxtLog Err: " + ex2.Message + "] "; }

                                // 3. TF Task
                                bool hasTf = false;
                                string tfStart = "", tfEnd = "", tfMode = "", tfImgPath = "";
                                double tfW = 0, tfL = 0, tfProd = 0;
                                int tfCompleted = 0;

                                try
                                {
                                    using (var cmd = new SQLiteCommand("SELECT * FROM historialtf WHERE JobName LIKE '%" + safeName + "%' ORDER BY StartTime DESC LIMIT 1", conn))
                                    {
                                        using (var r = cmd.ExecuteReader())
                                        {
                                            if (r.Read())
                                            {
                                                hasTf = true;
                                                tfStart = r["StartTime"].ToString();
                                                tfEnd = r["EndTime"].ToString();
                                                tfMode = r["Mode"].ToString();
                                                tfImgPath = r["LocalImagePath"].ToString();
                                                double.TryParse(r["Width"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tfW);
                                                double.TryParse(r["Length"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tfL);
                                                double.TryParse(r["ProductionRatio"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tfProd);
                                                try { tfCompleted = Convert.ToInt32(r["Completed"]); } catch {}
                                            }
                                        }
                                    }
                                    debugLog += "[TfTask OK] ";
                                }
                                catch (Exception ex3) { debugLog += "[TfTask Err: " + ex3.Message + "] "; }

                                string txtJson = "null";
                                if (hasTxt)
                                {
                                    txtJson = "{"
                                        + "\"startTime\":\"" + HttpUtility.JavaScriptStringEncode(txtStart) + "\","
                                        + "\"endTime\":\"" + HttpUtility.JavaScriptStringEncode(txtEnd) + "\","
                                        + "\"mode\":\"" + HttpUtility.JavaScriptStringEncode(txtMode) + "\","
                                        + "\"width\":" + txtW.ToString(CultureInfo.InvariantCulture) + ","
                                        + "\"length\":" + txtL.ToString(CultureInfo.InvariantCulture) + ","
                                        + "\"copies\":" + txtCopies + ","
                                        + "\"completed\":" + txtCompleted + ","
                                        + "\"productionRatio\":" + txtProd.ToString(CultureInfo.InvariantCulture) + ","
                                        + "\"totalPass\":" + txtTPass + ","
                                        + "\"maxPass\":" + txtMPass
                                        + "}";
                                }

                                string tfJson = "null";
                                if (hasTf)
                                {
                                    tfJson = "{"
                                        + "\"startTime\":\"" + HttpUtility.JavaScriptStringEncode(tfStart) + "\","
                                        + "\"endTime\":\"" + HttpUtility.JavaScriptStringEncode(tfEnd) + "\","
                                        + "\"mode\":\"" + HttpUtility.JavaScriptStringEncode(tfMode) + "\","
                                        + "\"width\":" + tfW.ToString(CultureInfo.InvariantCulture) + ","
                                        + "\"length\":" + tfL.ToString(CultureInfo.InvariantCulture) + ","
                                        + "\"completed\":" + tfCompleted + ","
                                        + "\"productionRatio\":" + tfProd.ToString(CultureInfo.InvariantCulture) + ","
                                        + "\"localImagePath\":\"" + HttpUtility.JavaScriptStringEncode(tfImgPath) + "\""
                                        + "}";
                                }

                                jsonResult = "{"
                                    + "\"machineName\":\"" + HttpUtility.JavaScriptStringEncode(machineName) + "\","
                                    + "\"fileName\":\"" + HttpUtility.JavaScriptStringEncode(ripFileName) + "\","
                                    + "\"state\":\"" + HttpUtility.JavaScriptStringEncode(ripState) + "\","
                                    + "\"startTime\":\"" + HttpUtility.JavaScriptStringEncode(ripStartTime) + "\","
                                    + "\"width\":" + ripWidth.ToString(CultureInfo.InvariantCulture) + ","
                                    + "\"length\":" + ripLength.ToString(CultureInfo.InvariantCulture) + ","
                                    + "\"copias\":" + ripCopias + ","
                                    + "\"debugLog\":\"" + HttpUtility.JavaScriptStringEncode(debugLog) + "\","
                                    + "\"txtLog\":" + txtJson + ","
                                    + "\"tfTask\":" + tfJson
                                    + "}";
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("DEBUG print_details caught: " + ex.ToString());
                            jsonResult = string.Format("{{\"error\":\"{0}\"}}", HttpUtility.JavaScriptStringEncode(ex.ToString()));
                        }
                    }

                    SendJsonResponse(response, jsonResult);
                }
                else if (path.StartsWith("/api/daily_details"))
                {
                    string dateParam = request.QueryString["date"];
                    if (string.IsNullOrWhiteSpace(dateParam)) dateParam = DateTime.Now.ToString("yyyy-MM-dd");
                    string machineParam = request.QueryString["machine"];
                    string machineName = Environment.MachineName;

                    var jsonList = new List<string>();
                    bool returnEmpty = (!string.IsNullOrWhiteSpace(machineParam) && !machineParam.Equals(machineName, StringComparison.OrdinalIgnoreCase));

                    if (!returnEmpty)
                    {
                        try
                        {
                            using (var conn = new SQLiteConnection(DatabaseManager.connectionString))
                            {
                                conn.Open();
                                string query = "SELECT * FROM riplog WHERE (StartTime LIKE '" + dateParam.Replace("-", "/") + "%' OR StartTime LIKE '" + dateParam.Replace("/", "-") + "%') ORDER BY StartTime DESC";
                                using (var cmd = new SQLiteCommand(query, conn))
                                {
                                    using (var reader = cmd.ExecuteReader())
                                    {
                                        while (reader.Read())
                                        {
                                            string fn = reader["FileName"].ToString();
                                            string st = reader["StartTime"].ToString();
                                            string state = reader["State"].ToString();
                                            double w = 0, l = 0;
                                            double.TryParse(reader["Width"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out w);
                                            double.TryParse(reader["Length"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out l);
                                            int copias = 1;
                                            try { if (reader["Copias"] != DBNull.Value) copias = Convert.ToInt32(reader["Copias"]); } catch {}

                                            // Subquery TXT Log
                                            string txtJson = "null";
                                            string safeFn = fn.Replace("'", "''");
                                            string txtQuery = "SELECT * FROM logtxt WHERE JobName LIKE '%" + safeFn + "%' AND (StartTime LIKE '" + dateParam.Replace("-", "/") + "%' OR StartTime LIKE '" + dateParam.Replace("/", "-") + "%') ORDER BY StartTime DESC LIMIT 1";
                                            using (var cmdTxt = new SQLiteCommand(txtQuery, conn))
                                            {
                                                using (var rTxt = cmdTxt.ExecuteReader())
                                                {
                                                    if (rTxt.Read())
                                                    {
                                                        double tw = 0, tl = 0, tprod = 0;
                                                        double.TryParse(rTxt["Width"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tw);
                                                        double.TryParse(rTxt["Length"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tl);
                                                        double.TryParse(rTxt["ProductionRatio"].ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out tprod);
                                                        txtJson = string.Format(CultureInfo.InvariantCulture,
                                                            "{{\"startTime\":\"{0}\",\"endTime\":\"{1}\",\"mode\":\"{2}\",\"width\":{3},\"length\":{4},\"copies\":{5},\"completed\":{6},\"productionRatio\":{7},\"totalPass\":{8},\"maxPass\":{9}}}",
                                                            rTxt["StartTime"].ToString(), rTxt["EndTime"].ToString(),
                                                            HttpUtility.JavaScriptStringEncode(rTxt["Mode"].ToString()),
                                                            tw, tl, Convert.ToInt32(rTxt["Copies"]), Convert.ToInt32(rTxt["Completed"]),
                                                            tprod, Convert.ToInt32(rTxt["TotalPass"]), Convert.ToInt32(rTxt["MaxPass"]));
                                                    }
                                                }
                                            }

                                            jsonList.Add("{"
                                                + "\"fileName\":\"" + HttpUtility.JavaScriptStringEncode(fn) + "\","
                                                + "\"state\":\"" + HttpUtility.JavaScriptStringEncode(state) + "\","
                                                + "\"startTime\":\"" + HttpUtility.JavaScriptStringEncode(st) + "\","
                                                + "\"width\":" + w.ToString(CultureInfo.InvariantCulture) + ","
                                                + "\"length\":" + l.ToString(CultureInfo.InvariantCulture) + ","
                                                + "\"copias\":" + copias + ","
                                                + "\"machineName\":\"" + HttpUtility.JavaScriptStringEncode(machineName) + "\","
                                                + "\"txtLog\":" + txtJson
                                                + "}");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("API Daily Details Error: " + ex.Message);
                        }
                    }

                    string printedJson = "[" + string.Join(",", jsonList.ToArray()) + "]";
                    string json = string.Format("{{\"status\":\"success\",\"queryDate\":\"{0}\",\"machineName\":\"{1}\",\"jobs\":{2}}}",
                        dateParam, HttpUtility.JavaScriptStringEncode(machineName), printedJson);

                    SendJsonResponse(response, json);
                }
                else if (path.StartsWith("/api/print"))
                {
                    List<PrintedJob> allPrinted = new List<PrintedJob>();
                    string dataDir = Path.GetDirectoryName(printExpPath);
                    string[] tfFiles = { "printTask.tf", "recordTask.tf", "HistoryTask.tf", "fileTask.tf" };
                    HashSet<string> seenJobs = new HashSet<string>();

                    foreach (string tfFile in tfFiles) {
                        string fullPath = Path.Combine(dataDir, tfFile);
                        if (File.Exists(fullPath)) {
                            var jobs = ParseHistoryTask(fullPath);
                            foreach(var job in jobs) {
                                job.SourceFile = tfFile;
                                if (!string.IsNullOrEmpty(job.BmpPath) && !File.Exists(job.BmpPath)) {
                                    job.BmpPath = "";
                                }
                                string key = !string.IsNullOrWhiteSpace(job.UID) && job.UID.Length > 10 ? job.UID : job.Name + job.StartTime;
                                if (!seenJobs.Contains(key)) {
                                    allPrinted.Add(job);
                                    seenJobs.Add(key);
                                }
                            }
                        }
                    }

                    List<string> printedJsonList = new List<string>();
                    foreach(var job in allPrinted) {
                        printedJsonList.Add(string.Format("{{\"name\":\"{0}\",\"start\":\"{1}\",\"end\":\"{2}\",\"w\":\"{3}\",\"l\":\"{4}\",\"mode\":\"{5}\",\"copies\":{6},\"completed\":{7},\"production\":{8},\"speedM2h\":{9},\"uid\":\"{10}\",\"prtPath\":\"{11}\",\"bmpPath\":\"{12}\",\"sourceFile\":\"{13}\"}}", 
                            HttpUtility.JavaScriptStringEncode(job.Name),
                            job.StartTime, job.EndTime, job.Width, job.Length,
                            HttpUtility.JavaScriptStringEncode(job.Mode),
                            job.RequiredCopies, job.Completed, job.ProductionRatio.ToString(System.Globalization.CultureInfo.InvariantCulture), 
                            job.SpeedM2h.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            HttpUtility.JavaScriptStringEncode(job.UID ?? ""),
                            HttpUtility.JavaScriptStringEncode(job.PrtPath ?? ""),
                            HttpUtility.JavaScriptStringEncode(job.BmpPath ?? ""),
                            HttpUtility.JavaScriptStringEncode(job.SourceFile ?? "")
                            ));
                    }
                    
                    string machineName = Environment.MachineName;
                    string printedJson = "[" + string.Join(",", printedJsonList.ToArray()) + "]";
                    string json = string.Format("{{\"machineName\":\"{0}\", \"printedJobs\":{1}}}", 
                        HttpUtility.JavaScriptStringEncode(machineName), 
                        printedJson);

                    SendJsonResponse(response, json);
                }
                else if (path == "/api/image")
                {
                    string imgPath = request.QueryString["path"];
                    if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
                    {
                        try
                        {
                            byte[] imgBytes;
                            using (var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                imgBytes = new byte[fs.Length];
                                fs.Read(imgBytes, 0, (int)fs.Length);
                            }
                            response.ContentType = "image/bmp";
                            response.ContentLength64 = imgBytes.Length;
                            response.OutputStream.Write(imgBytes, 0, imgBytes.Length);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error al enviar imagen: " + ex.Message);
                            response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        }
                    }
                    else
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                }
                else if (path.StartsWith("/img_cache/"))
                {
                    string filename = path.Substring("/img_cache/".Length);
                    string localPath = Path.Combine(DatabaseManager.imgCacheDir, filename);

                    if (File.Exists(localPath))
                    {
                        try
                        {
                            byte[] imgBytes;
                            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                imgBytes = new byte[fs.Length];
                                fs.Read(imgBytes, 0, (int)fs.Length);
                            }
                            // Detect content type
                            string ext = Path.GetExtension(localPath).ToLower();
                            string cType = "image/jpeg";
                            if (ext == ".png") cType = "image/png";
                            else if (ext == ".bmp") cType = "image/bmp";
                            
                            response.ContentType = cType;
                            response.ContentLength64 = imgBytes.Length;
                            response.OutputStream.Write(imgBytes, 0, imgBytes.Length);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Error enviando de img_cache: " + ex.Message);
                            response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        }
                    }
                    else
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                }
                else if (path == "/api/printlog")
                {
                    string filterDate = request.QueryString["date"];
                    string filterStart = request.QueryString["start"];
                    string filterEnd = request.QueryString["end"];
                    string filterName = request.QueryString["filename"];

                    List<string> jsonList = new List<string>();
                    
                    try 
                    {
                        using (var conn = new SQLiteConnection(DatabaseManager.connectionString))
                        {
                            conn.Open();
                            
                            string query = "SELECT * FROM logtxt WHERE 1=1";
                            
                            if (!string.IsNullOrEmpty(filterDate)) {
                                query += " AND StartTime LIKE @date";
                            }
                            if (!string.IsNullOrEmpty(filterStart)) {
                                query += " AND StartTime >= @start";
                            }
                            if (!string.IsNullOrEmpty(filterEnd)) {
                                query += " AND StartTime <= @end";
                            }
                            if (!string.IsNullOrEmpty(filterName)) {
                                query += " AND JobName LIKE @name";
                            }

                            query += " ORDER BY StartTime DESC LIMIT 500"; // Safety limit to avoid huge payload crashes on web clients

                            using (var cmd = new SQLiteCommand(query, conn))
                            {
                                if (!string.IsNullOrEmpty(filterDate)) cmd.Parameters.AddWithValue("@date", filterDate + "%");
                                if (!string.IsNullOrEmpty(filterStart)) cmd.Parameters.AddWithValue("@start", filterStart + " 00:00:00");
                                if (!string.IsNullOrEmpty(filterEnd)) cmd.Parameters.AddWithValue("@end", filterEnd + " 23:59:59");
                                if (!string.IsNullOrEmpty(filterName)) cmd.Parameters.AddWithValue("@name", "%" + filterName + "%");

                                using (var reader = cmd.ExecuteReader())
                                {
                                    while (reader.Read())
                                    {
                                        string name = reader["JobName"].ToString();
                                        string sTime = reader["StartTime"].ToString().Replace("-", "/"); // Frontend Expects 2026/03/13 20:24:00
                                        string eTime = reader["EndTime"].ToString().Replace("-", "/");
                                        string w = reader["Width"].ToString();
                                        string l = reader["Length"].ToString();
                                        string mode = reader["Mode"].ToString();
                                        int copies = Convert.ToInt32(reader["Copies"]);
                                        int completed = Convert.ToInt32(reader["Completed"]);
                                        double prod = Convert.ToDouble(reader["ProductionRatio"]);
                                        int tPass = Convert.ToInt32(reader["TotalPass"]);
                                        int mPass = Convert.ToInt32(reader["MaxPass"]);

                                        jsonList.Add(string.Format("{{\"name\":\"{0}\",\"start\":\"{1}\",\"end\":\"{2}\",\"w\":\"{3}\",\"l\":\"{4}\",\"mode\":\"{5}\",\"copies\":{6},\"completed\":{7},\"production\":{8},\"totalPass\":{9},\"maxPass\":{10}}}", 
                                            HttpUtility.JavaScriptStringEncode(name),
                                            sTime, eTime, w, l,
                                            HttpUtility.JavaScriptStringEncode(mode),
                                            copies, completed, prod.ToString(CultureInfo.InvariantCulture),
                                            tPass, mPass
                                        ));
                                    }
                                }
                            }
                        }
                    } 
                    catch (Exception ex) 
                    {
                        Console.WriteLine("DB Error api/printlog: " + ex.Message);
                    }

                    string machineName = Environment.MachineName;
                    string printedJson = "[" + string.Join(",", jsonList.ToArray()) + "]";
                    string json = string.Format("{{\"machineName\":\"{0}\", \"logJobs\":{1}}}", 
                        HttpUtility.JavaScriptStringEncode(machineName), printedJson);

                    SendJsonResponse(response, json);
                }
                else if (path == "/api/resumen")
                {
                    Dictionary<string, DaySummary> days = new Dictionary<string, DaySummary>();
                    
                    try
                    {
                        using (var conn = new SQLiteConnection(DatabaseManager.connectionString))
                        {
                            conn.Open();

                            // 1. TF Logs (historialtf)
                            string sqlTf = @"SELECT SUBSTR(StartTime, 1, 10) as DateStr, ProductionRatio, RequiredCopies, Width, Length 
                                             FROM historialtf 
                                             WHERE StartTime IS NOT NULL AND JobName IS NOT NULL";
                            // Nota: En historialtf RequiredCopies no existe nativo, asumimos copiando la logica o usariamos Copies si lo tuvieramos.
                            // HistorialTF no guardó copies... oh my. Let's adapt. Wait, tf logic used copies. 
                            // We need to fetch it correctly. Wait, historytask TF file doesnt have copies directly accessible in DB schema we made, 
                            // we need to fix that or assume Copies = 1. I will assume copies=1 for TF in DB or adjust if needed.
                            
                            string sqlTfReal = "SELECT SUBSTR(StartTime, 1, 10) as DateStr, ProductionRatio, Width, Length FROM historialtf WHERE StartTime IS NOT NULL";
                            using (var cmd = new SQLiteCommand(sqlTfReal, conn))
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string dStr = reader["DateStr"].ToString();
                                    if (string.IsNullOrEmpty(dStr)) continue;
                                    
                                    if (!days.ContainsKey(dStr)) days[dStr] = new DaySummary{ Date = dStr };
                                    
                                    float wF = Convert.ToSingle(reader["Width"]);
                                    float lF = Convert.ToSingle(reader["Length"]);
                                    float prod = Convert.ToSingle(reader["ProductionRatio"]);
                                    
                                    float ml = (lF / 100f) * 1f; // Copies asumido 1 por schema TF
                                    float m2 = (wF * lF / 10000f) * 1f;
                                    float ratio = (float)(prod / 100.0);
                                    
                                    if (prod >= 95.0) {
                                        days[dStr].TfUsefulML += ml * ratio;
                                        days[dStr].TfUsefulM2 += m2 * ratio;
                                    } else {
                                        days[dStr].TfWasteML += ml * ratio;
                                        days[dStr].TfWasteM2 += m2 * ratio;
                                    }
                                }
                            }

                            // 2. TXT Logs (logtxt)
                            string sqlTxt = "SELECT SUBSTR(StartTime, 1, 10) as DateStr, ProductionRatio, Completed, Copies, Width, Length FROM logtxt WHERE StartTime IS NOT NULL";
                            using (var cmd = new SQLiteCommand(sqlTxt, conn))
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string dStr = reader["DateStr"].ToString();
                                    if (string.IsNullOrEmpty(dStr)) continue;
                                    if (!days.ContainsKey(dStr)) days[dStr] = new DaySummary{ Date = dStr };
                                    
                                    float wF = Convert.ToSingle(reader["Width"]);
                                    float lF = Convert.ToSingle(reader["Length"]);
                                    float prod = Convert.ToSingle(reader["ProductionRatio"]);
                                    int completed = Convert.ToInt32(reader["Completed"]);
                                    int copies = Convert.ToInt32(reader["Copies"]);
                                    
                                    float ml = (lF / 100f) * copies;
                                    float m2 = (wF * lF / 10000f) * copies;
                                    
                                    float ratio = completed == 1 ? 1.0f : (float)(prod / 100.0);
                                    float productionLogic = completed == 1 ? 100f : prod;

                                    if (productionLogic >= 95.0f) {
                                        days[dStr].TxtUsefulML += ml * ratio;
                                        days[dStr].TxtUsefulM2 += m2 * ratio;
                                    } else {
                                        days[dStr].TxtWasteML += ml * ratio;
                                        days[dStr].TxtWasteM2 += m2 * ratio;
                                    }
                                }
                            }

                            // 3. RIP Logs (riplog)
                            string sqlRip = "SELECT SUBSTR(StartTime, 1, 10) as DateStr, Width, Length FROM riplog WHERE StartTime IS NOT NULL AND State = 'RIP'";
                            using (var cmd = new SQLiteCommand(sqlRip, conn))
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string dStr = reader["DateStr"].ToString();
                                    if (string.IsNullOrEmpty(dStr)) continue;
                                    if (!days.ContainsKey(dStr)) days[dStr] = new DaySummary{ Date = dStr };
                                    
                                    float wF = Convert.ToSingle(reader["Width"]);
                                    float lF = Convert.ToSingle(reader["Length"]);
                                    int copies = 1; // RIP no tiene multiples copias tipicas en la celda
                                    
                                    float ml = (lF / 100f) * copies;
                                    float m2 = (wF * lF / 10000f) * copies;

                                    days[dStr].RipML += ml;
                                    days[dStr].RipM2 += m2;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("DB Error api/resumen: " + ex.Message);
                    }

                    List<string> jsonList = new List<string>();
                    foreach(var kvp in days.OrderByDescending(x => x.Key)) {
                        var val = kvp.Value;
                        jsonList.Add(string.Format("{{\"date\":\"{0}\",\"ripML\":{1},\"ripM2\":{2},\"tfUsefulML\":{3},\"tfUsefulM2\":{4},\"tfWasteML\":{5},\"tfWasteM2\":{6},\"txtUsefulML\":{7},\"txtUsefulM2\":{8},\"txtWasteML\":{9},\"txtWasteM2\":{10}}}",
                            val.Date, 
                            val.RipML.ToString(CultureInfo.InvariantCulture), val.RipM2.ToString(CultureInfo.InvariantCulture),
                            val.TfUsefulML.ToString(CultureInfo.InvariantCulture), val.TfUsefulM2.ToString(CultureInfo.InvariantCulture),
                            val.TfWasteML.ToString(CultureInfo.InvariantCulture), val.TfWasteM2.ToString(CultureInfo.InvariantCulture),
                            val.TxtUsefulML.ToString(CultureInfo.InvariantCulture), val.TxtUsefulM2.ToString(CultureInfo.InvariantCulture),
                            val.TxtWasteML.ToString(CultureInfo.InvariantCulture), val.TxtWasteM2.ToString(CultureInfo.InvariantCulture)
                        ));
                    }
                    
                    string machineName = Environment.MachineName;
                    string resJson = "[" + string.Join(",", jsonList.ToArray()) + "]";
                    string json = string.Format("{{\"machineName\":\"{0}\", \"summary\":{1}}}", 
                        HttpUtility.JavaScriptStringEncode(machineName), resJson);

                    SendJsonResponse(response, json);
                }
                else if (path == "/api/sync")
                {
                    try
                    {
                        string ripDirSync = @"C:\Program Files\SAi\FlexiPRINT 22 WitColor Edition\Jobs and Settings";
                        string printExpDirSync = @"C:\Program Files (x86)\PrintExp_X64\Data";
                        string logTxtDirSync = @"C:\Program Files (x86)\PrintExp_X64\Data";

                        string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                        if (File.Exists(cfgPath))
                        {
                            try
                            {
                                string[] lines = File.ReadAllLines(cfgPath, Encoding.UTF8);
                                if (lines.Length >= 1 && !string.IsNullOrWhiteSpace(lines[0])) ripDirSync = lines[0].Trim();
                                if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1])) printExpDirSync = lines[1].Trim();
                                if (lines.Length >= 3 && !string.IsNullOrWhiteSpace(lines[2])) logTxtDirSync = lines[2].Trim();
                            }
                            catch {}
                        }

                        DatabaseManager.SyncRipLog(ripDirSync);
                        DatabaseManager.SyncHistorialTf(printExpDirSync);
                        DatabaseManager.SyncLogTxt(logTxtDirSync, printExpDirSync);

                        string machineName = Environment.MachineName;
                        string json = string.Format("{{\"status\":\"ok\",\"message\":\"Sincronización completada exitosamente\",\"machineName\":\"{0}\"}}", HttpUtility.JavaScriptStringEncode(machineName));
                        SendJsonResponse(response, json);
                    }
                    catch (Exception ex)
                    {
                        string json = string.Format("{{\"status\":\"error\",\"message\":\"{0}\"}}", HttpUtility.JavaScriptStringEncode(ex.Message));
                        SendJsonResponse(response, json);
                    }
                }
                else if (path == "/resumen.html" || path == "/resumen")
                {
                    SendHtmlResponse(response, "resumen.html");
                }
                else if (path == "/printlog.html" || path == "/printlog")
                {
                    SendHtmlResponse(response, "printlog.html");
                }
                else if (path == "/printexp.html" || path == "/printexp")
                {
                    SendHtmlResponse(response, "printexp.html");
                }
                else if (path == "/sql_riplog.html" || path == "/sql_riplog")
                {
                    SendHtmlResponse(response, "sql_riplog.html");
                }
                else
                {
                    SendHtmlResponse(response, "sql_riplog.html");
                }
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                try {
                    byte[] error = Encoding.UTF8.GetBytes(ex.Message);
                    response.OutputStream.Write(error, 0, error.Length);
                    response.OutputStream.Close();
                } catch {}
            }
        }

        public static List<PrintedJob> ParseLogTxt(string directory)
        {
            List<PrintedJob> jobs = new List<PrintedJob>();
            if (!Directory.Exists(directory)) return jobs;

            // Extraer infinitamente desde cualquier subdirectorio (Ej: la carpeta TODOS)
            var dirInfo = new DirectoryInfo(directory);
            var allFiles = dirInfo.GetFiles("Log[????_??_??].txt", SearchOption.AllDirectories);
            
            // Para proteger de bloqueos RAM, se filtran UNICAMENTE los ultimos 5 dias por cada MAQUINA en base a su subcarpeta
            var logFiles = allFiles.GroupBy(f => f.DirectoryName)
                                   .SelectMany(g => g.OrderByDescending(f => f.Name).Take(5))
                                   .ToList();

            // Usar pagina de codigos para Chino Simplificado GB2312 (Windows 936), con Fallback a ASCII/UTF8 si el PC es muy viejo
            Encoding gb2312;
            try { gb2312 = Encoding.GetEncoding(936); }
            catch { gb2312 = Encoding.Default; }

            // Puede venir como [14:03:58] o [2026/03/06 14:03:58]
            Regex timeRegex = new Regex(@"(\d{2}:\d{2}:\d{2})\]");
            // Puede estar entre corchetes o el nombre crudo de prt
            Regex fileRegex = new Regex(@"作业【([^】]+)】|启动任务：([^ ]+)");
            // Nueva versión de Info: 任务精度:360 X 600,图像大小:299.86mm X 16.93mm,颜色数:0,模式:1Pass
            Regex infoRegex = new Regex(@"图像大小:([\d.]+)mm X ([\d.]+)mm(.*?([0-9a-zA-Z_]+Pass)[^,]*)?");
            Regex totalPassRegex = new Regex(@"pInitParam->nTotalPrintPass=(\d+)");
            Regex curPassRegex = new Regex(@"nCurPass=(\d+)");
            string startPattern = "CPrintControl::打印线程.............................开始";
            string endPattern = "CPrintControl::打印线程.............................结束";
            string cancelPattern = "打印结果取消或者报错";

            foreach (var file in logFiles)
            {
                // Extraer fecha YY_MM_DD del nombre del archivo (Case Insensitive)
                Match dateMatch = Regex.Match(file.Name, @"Log\[(\d{4})_(\d{2})_(\d{2})\]\.txt", RegexOptions.IgnoreCase);
                if (!dateMatch.Success) continue;
                string datePrefix = string.Format("{0}/{1}/{2}", dateMatch.Groups[1].Value, dateMatch.Groups[2].Value, dateMatch.Groups[3].Value);

                try
                {
                    List<string> currentJobLines = new List<string>();
                    bool inJob = false;

                    using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, gb2312))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.Contains(startPattern))
                            {
                                inJob = true;
                                currentJobLines.Clear();
                                currentJobLines.Add(line);
                            }
                            else if (inJob && (line.Contains(endPattern) || line.Contains(cancelPattern)))
                            {
                                currentJobLines.Add(line);
                                
                                // Process isolated job memory block
                                PrintedJob job = ProcessTxtJobBlock(currentJobLines, datePrefix, timeRegex, fileRegex, infoRegex, totalPassRegex, curPassRegex, cancelPattern);
                                if (!string.IsNullOrEmpty(job.StartTime) && !string.IsNullOrEmpty(job.Name) && job.Name != "No encontrada")
                                {
                                    jobs.Add(job);
                                }

                                inJob = false;
                                currentJobLines.Clear();
                            }
                            else if (inJob)
                            {
                                currentJobLines.Add(line);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error leyendo " + file.Name + ": " + ex.Message);
                }
            }
            return jobs;
        }

        static PrintedJob ProcessTxtJobBlock(List<string> lines, string dateStr, Regex timeRegex, Regex fileRegex, Regex infoRegex, Regex totalPassRegex, Regex curPassRegex, string cancelPattern)
        {
            PrintedJob job = new PrintedJob { Name = "No encontrada", Mode = "Desconocido", StartTime = "", EndTime = "" };
            int maxNcurPass = -1;
            int totalPrintPass = 0;
            bool isCancelled = false;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];

                if (i == 0 || i == lines.Count - 1)
                {
                    Match mTime = timeRegex.Match(line);
                    if (mTime.Success) {
                        string ts = dateStr + " " + mTime.Groups[1].Value;
                        if (i == 0) job.StartTime = ts;
                        else job.EndTime = ts;
                    }
                }

                if (job.Name == "No encontrada") {
                    Match mFile = fileRegex.Match(line);
                    if (mFile.Success) {
                        job.Name = !string.IsNullOrEmpty(mFile.Groups[1].Value) ? mFile.Groups[1].Value : mFile.Groups[2].Value;
                    } 
                    else if (line.Contains(":\\") && line.EndsWith(".prt", StringComparison.OrdinalIgnoreCase)) {
                        // Respaldo de busqueda de ruta windows tipica si el regex falla
                        int idx = line.IndexOf(":\\");
                        if (idx >= 1) {
                            string pth = line.Substring(idx - 1);
                            int spc = pth.IndexOf(" ");
                            if (spc > 0) pth = pth.Substring(0, spc);
                            job.Name = pth.Trim();
                        }
                    }
                }

                Match mInfo = infoRegex.Match(line);
                if (mInfo.Success) {
                    float w = 0f;
                    float l = 0f;
                    float.TryParse(mInfo.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out w);
                    float.TryParse(mInfo.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out l);
                    job.Width = w.ToString(CultureInfo.InvariantCulture);
                    job.Length = l.ToString(CultureInfo.InvariantCulture);
                    if (mInfo.Groups.Count >= 5 && !string.IsNullOrEmpty(mInfo.Groups[4].Value)) {
                        job.Mode = mInfo.Groups[4].Value;
                    }
                }

                Match mTot = totalPassRegex.Match(line);
                if (mTot.Success) int.TryParse(mTot.Groups[1].Value, out totalPrintPass);

                Match mCur = curPassRegex.Match(line);
                if (mCur.Success) {
                    int cur = 0;
                    if (int.TryParse(mCur.Groups[1].Value, out cur)) {
                        if (cur > maxNcurPass) maxNcurPass = cur;
                    }
                }

                if (line.Contains(cancelPattern)) isCancelled = true;
            }
            
            job.Name = Path.GetFileName(job.Name); // solo el nombre final
            
            // Calculo de Mates C++
            int copies = 1;
            if (totalPrintPass > 0 && maxNcurPass >= 0)
            {
                double quotient = (double)maxNcurPass / totalPrintPass;
                copies = quotient < 1.0 ? 1 : (int)Math.Round(quotient);
            }
            
            // Re-escaneo de respaldo si Job==No encontrada: a veces el Log no dice el nombre pero esta en algun path PRT general
            if (job.Name == "No encontrada")
            {
                foreach(string l in lines) {
                    if (l.Contains(":\\") && l.EndsWith("prt", StringComparison.OrdinalIgnoreCase)) {
                        int st = l.IndexOf("C:\\");
                        if (st == -1) st = l.IndexOf("D:\\");
                        if (st == -1) st = l.IndexOf("E:\\");
                        if (st >= 0) {
                            string pt = l.Substring(st);
                            int sp = pt.IndexOf(" ");
                            if (sp > 0) pt = pt.Substring(0, sp);
                            job.Name = Path.GetFileName(pt.Trim());
                            break;
                        }
                    }
                }
            }

            job.RequiredCopies = copies;

            // Estado (1: Completado, 0: Cancelado/Interrumpido)
            if (isCancelled) job.Completed = 0;
            else if (maxNcurPass != -1 && totalPrintPass > 0 && maxNcurPass >= totalPrintPass - 2) job.Completed = 1;
            else job.Completed = 0;

            job.SpeedM2h = totalPrintPass; // Usamos esto de placeholder para exportar
            job.Passes = maxNcurPass >= 0 ? maxNcurPass : 0; // Usado de placeholder para exportar max cur

            if (totalPrintPass > 0) {
                double realProd = ((double)maxNcurPass / (totalPrintPass * copies)) * 100.0;
                if (realProd > 100) realProd = 100.0;
                if (realProd < 0) realProd = 0.0;
                job.ProductionRatio = realProd;
            } else {
                job.ProductionRatio = job.Completed == 1 ? 100.0 : 0.0;
            }

            return job;
        }

        public static List<PrintedJob> ParseHistoryTask(string path)
        {
            List<PrintedJob> jobs = new List<PrintedJob>();
            try {
                if (!File.Exists(path)) return jobs;
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length < 20) return jobs;

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    byte[] header = br.ReadBytes(12);
                    if (header.Length < 12) return jobs;

                    while (fs.Position < fs.Length)
                    {
                        long bytesLeft = fs.Length - fs.Position;
                        if (bytesLeft < 4) break;

                        long recStart = fs.Position;
                        int totalLen = br.ReadInt32();
                        long nextRec = recStart + 4 + totalLen;

                        if (nextRec > fs.Length) break;

                        try
                        {
                            PrintedJob job = new PrintedJob();
                            int codImagen = br.ReadInt32();
                            
                            int nameLen = br.ReadInt32();
                            job.Name = ReadStringFromBytes(br, nameLen);
                            
                            int nullLen = br.ReadInt32();
                            ReadStringFromBytes(br, nullLen);

                            int prtPathLen = br.ReadInt32();
                            job.PrtPath = ReadStringFromBytes(br, prtPathLen);

                            int bmpPathLen = br.ReadInt32();
                            job.BmpPath = ReadStringFromBytes(br, bmpPathLen);

                            br.ReadBytes(5);

                            int anchoPx = br.ReadInt32();
                            int largoPx = br.ReadInt32();
                            br.ReadBytes(8);
                            int resX = br.ReadInt32();
                            int resY = br.ReadInt32();
                            br.ReadBytes(44);

                            double anchoMm = br.ReadDouble();
                            br.ReadBytes(8);
                            double largoMm = br.ReadDouble();
                            br.ReadBytes(4);

                            job.RequiredCopies = br.ReadInt32();
                            br.ReadBytes(28);

                            job.Completed = br.ReadInt32();
                            br.ReadBytes(8);

                            job.ProductionRatio = br.ReadDouble();
                            br.ReadBytes(28);

                            int tiempoTranscurrido1 = br.ReadInt32();
                            br.ReadBytes(276);

                            job.SpeedM2h = br.ReadDouble();

                            int modoLen = br.ReadInt32();
                            job.Mode = ReadStringFromBytes(br, modoLen);

                            byte[] rawFecha1 = br.ReadBytes(16);
                            job.StartTime = InterpretarFecha(rawFecha1);

                            byte[] rawFecha2 = br.ReadBytes(16);
                            job.EndTime = InterpretarFecha(rawFecha2);

                            br.ReadBytes(8);
                            int tiempoTranscurrido2 = br.ReadInt32();

                            double anchoMm2 = br.ReadDouble();
                            double largoMm2 = br.ReadDouble();
                            br.ReadBytes(12);

                            int uidLen = br.ReadInt32();
                            job.UID = ReadStringFromBytes(br, uidLen);

                            job.Width = Math.Abs(anchoMm) > 0.1 && Math.Abs(anchoMm) < 10000 ? (anchoMm / 10).ToString("F2") : "-";
                            job.Length = Math.Abs(largoMm) > 0.1 && Math.Abs(largoMm) < 10000 ? (largoMm / 10).ToString("F2") : "-";

                            if (!string.IsNullOrEmpty(job.Name)) {
                                jobs.Add(job);
                            }
                        }
                        catch (Exception innerEx)
                        {
                            Console.WriteLine("Error leyendo registro en offset " + recStart + ": " + innerEx.Message);
                        }
                        finally
                        {
                            fs.Seek(nextRec, SeekOrigin.Begin);
                        }
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine("Error parsing .tf (" + Path.GetFileName(path) + "): " + ex.Message);
            }
            return jobs;
        }

        static string ReadStringFromBytes(BinaryReader br, int len)
        {
            if (len <= 0 || len > 100000) return "";
            string raw = Encoding.UTF8.GetString(br.ReadBytes(len)).Split('\0')[0].Trim();
            return new string(Array.FindAll(raw.ToCharArray(), c => !char.IsControl(c)));
        }

        static string InterpretarFecha(byte[] data)
        {
            if (data.Length < 16) return "-";
            try
            {
                ushort y = BitConverter.ToUInt16(data, 0);
                ushort m = BitConverter.ToUInt16(data, 2);
                ushort d = BitConverter.ToUInt16(data, 6);
                ushort hh = BitConverter.ToUInt16(data, 8);
                ushort mm = BitConverter.ToUInt16(data, 10);
                ushort ss = BitConverter.ToUInt16(data, 12);
                if (y < 2000 || y > 2100) return "-";
                return string.Format("{0:D2}/{1:D2}/{2} {3:D2}:{4:D2}:{5:D2}", d, m, y, hh, mm, ss);
            }
            catch
            {
                return "-";
            }
        }

        static void SendJsonResponse(HttpListenerResponse response, string json)
        {
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        static void SendHtmlResponse(HttpListenerResponse response, string filename)
        {
            response.ContentType = "text/html";
            response.ContentEncoding = Encoding.UTF8;
            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
            string html = File.Exists(htmlPath) 
                ? File.ReadAllText(htmlPath, Encoding.UTF8) 
                : "<html><body><h1>" + filename + " no encontrado</h1></body></html>";
            
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        static string ReadTextAdaptive(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return "";

                long maxBytes = 1048576; // 1 MB máximo para evitar colapsar la memoria y el navegador
                long bytesToRead = Math.Min(fi.Length, maxBytes);
                long offset = fi.Length - bytesToRead;

                byte[] bytes = new byte[bytesToRead];
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Read(bytes, 0, (int)bytesToRead);
                }
                
                // Intenta interpretar como UTF-8 estricto
                try
                {
                    Encoding utf8 = new UTF8Encoding(false, true); // lanza excepcion si es invalido
                    return utf8.GetString(bytes);
                }
                catch
                {
                    // Si falla, es probablemente un ANSI antiguo como el generado por Windows/FlexiPrint
                    Encoding ansi = Encoding.GetEncoding("iso-8859-1"); 
                    return ansi.GetString(bytes);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error leyendo archivo: " + path + " - " + ex.Message);
                return "";
            }
        }

        public static string GetIsoDate(string dateStr) {
            if (string.IsNullOrEmpty(dateStr)) return "1970-01-01";
            Match m = Regex.Match(dateStr, @"(\d{2,4})/(\d{1,2})/(\d{2,4})");
            if (m.Success) {
                string p1 = m.Groups[1].Value;
                string p2 = m.Groups[2].Value.PadLeft(2,'0');
                string p3 = m.Groups[3].Value.PadLeft(2,'0');
                if (p1.Length == 4) return string.Format("{0}-{1}-{2}", p1, p2, p3);
                if (p3.Length == 4) return string.Format("{0}-{1}-{2}", p3, p2, p1.PadLeft(2,'0'));
            }
            return "1970-01-01";
        }
    }

    class PrintedJob
    {
        public string Name;
        public string StartTime;
        public string EndTime;
        public string Width;
        public string Length;
        public string Mode;
        public int RequiredCopies;
        public int Completed;
        public double ProductionRatio;
        public double SpeedM2h;
        public string PrtPath;
        public string BmpPath;
        public string UID;
        public string SourceFile;
        public int Passes { get; set; }
    }

    public class DaySummary 
    {
        public string Date { get; set; }
        public float RipML { get; set; }
        public float RipM2 { get; set; }
        public float TfUsefulML { get; set; }
        public float TfUsefulM2 { get; set; }
        public float TfWasteML { get; set; }
        public float TfWasteM2 { get; set; }
        public float TxtUsefulML { get; set; }
        public float TxtUsefulM2 { get; set; }
        public float TxtWasteML { get; set; }
        public float TxtWasteM2 { get; set; }
    }

    public static class DatabaseManager
    {
        public static readonly string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "impresiones.db");
        public static readonly string imgCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "img_cache");
        public static readonly string connectionString = "Data Source=" + dbPath + ";Version=3;DateTimeFormat=String;";

        public static void InitializeDatabase()
        {
            if (!Directory.Exists(imgCacheDir))
            {
                Directory.CreateDirectory(imgCacheDir);
            }

            if (!File.Exists(dbPath))
            {
                SQLiteConnection.CreateFile(dbPath);
            }

            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                string sql = @"
                    CREATE TABLE IF NOT EXISTS riplog (
                        Id TEXT PRIMARY KEY,
                        FileName TEXT,
                        State TEXT,
                        StartTime DATETIME,
                        Width REAL,
                        Length REAL,
                        SourceFile TEXT
                    );
                    CREATE TABLE IF NOT EXISTS historialtf (
                        Id TEXT PRIMARY KEY,
                        JobName TEXT,
                        StartTime DATETIME,
                        EndTime DATETIME,
                        Width REAL,
                        Length REAL,
                        Mode TEXT,
                        Completed INTEGER,
                        ProductionRatio REAL,
                        LocalImagePath TEXT
                    );
                    CREATE TABLE IF NOT EXISTS logtxt (
                        Id TEXT PRIMARY KEY,
                        JobName TEXT,
                        StartTime DATETIME,
                        EndTime DATETIME,
                        Width REAL,
                        Length REAL,
                        Mode TEXT,
                        Copies INTEGER,
                        Completed INTEGER,
                        ProductionRatio REAL,
                        TotalPass INTEGER,
                        MaxPass INTEGER
                    );
                ";
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.ExecuteNonQuery();
                        try { new SQLiteCommand("ALTER TABLE riplog ADD COLUMN Copias INTEGER DEFAULT 1", conn).ExecuteNonQuery(); } catch { }
                // Módulo 11: Saneamiento retroactivo de cadenas vacías que corrompen el ADO.NET DateTime parser
                try { new SQLiteCommand("UPDATE historialtf SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                try { new SQLiteCommand("UPDATE logtxt SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                // Saneamiento Nuclear de DATETIMEs
                string[] nuclearCmds = {
                    "UPDATE riplog SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE historialtf SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';",
                    "UPDATE logtxt SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE logtxt SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE logtxt SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';"
                };
                foreach(string nsql in nuclearCmds) {
                    try { new System.Data.SQLite.SQLiteCommand(nsql, conn).ExecuteNonQuery(); } catch {}
                }
        
        
                }
                
                string[] upgradeQueries = new string[] {
                    "ALTER TABLE riplog ADD COLUMN sincronizado INTEGER DEFAULT 0;",
                    "ALTER TABLE riplog ADD COLUMN detalle_id INTEGER DEFAULT 0;",
                    "ALTER TABLE historialtf ADD COLUMN sincronizado INTEGER DEFAULT 0;",
                    "ALTER TABLE historialtf ADD COLUMN detalle_id INTEGER DEFAULT 0;",
                    "ALTER TABLE logtxt ADD COLUMN sincronizado INTEGER DEFAULT 0;",
                    "ALTER TABLE logtxt ADD COLUMN detalle_id INTEGER DEFAULT 0;",
                    "ALTER TABLE historialtf ADD COLUMN FechaPrt DATETIME;",
                    "ALTER TABLE logtxt ADD COLUMN FechaPrt DATETIME;"
                };
                foreach(var uq in upgradeQueries) {
                    try { using (var cmd = new SQLiteCommand(uq, conn)) { cmd.ExecuteNonQuery();
                        try { new SQLiteCommand("ALTER TABLE riplog ADD COLUMN Copias INTEGER DEFAULT 1", conn).ExecuteNonQuery(); } catch { }
                // Módulo 11: Saneamiento retroactivo de cadenas vacías que corrompen el ADO.NET DateTime parser
                try { new SQLiteCommand("UPDATE historialtf SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                try { new SQLiteCommand("UPDATE logtxt SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                // Saneamiento Nuclear de DATETIMEs
                string[] nuclearCmds = {
                    "UPDATE riplog SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE historialtf SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';",
                    "UPDATE logtxt SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE logtxt SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE logtxt SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';"
                };
                foreach(string nsql in nuclearCmds) {
                    try { new System.Data.SQLite.SQLiteCommand(nsql, conn).ExecuteNonQuery(); } catch {}
                }
        
         } } catch {}
                }
            }
        }

        public static void SyncLoop(string ripLogDir, string tfDir, string logTxtDir)
        {
            while (true)
            {
                try
                {
                    SyncRipLog(ripLogDir);
                    SyncHistorialTf(tfDir);
                    SyncLogTxt(logTxtDir, tfDir);
                    SyncOracleApex();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error en SyncLoop: " + ex.Message);
                }
                Thread.Sleep(60000); // Sink every 1 minute
            }
        }

        private static void SyncOracleApex()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            string apexBaseUrl = "https://g0b98d8ee45b90d-db5ncy1.adb.us-sanjose-1.oraclecloudapps.com/ords/fullcolor";
            
            if (File.Exists(configPath)) {
                try {
                    string[] lines = File.ReadAllLines(configPath);
                    if (lines.Length >= 4 && !string.IsNullOrWhiteSpace(lines[3])) {
                        apexBaseUrl = lines[3].Trim();
                    }
                } catch { }
            }
            apexBaseUrl = apexBaseUrl.TrimEnd('/');

            try {
                // FORZAR TLS 1.2 (3072) PARA ORACLE CLOUD HTTPS EN .NET 4.0
                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;

                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string machineName = Environment.MachineName;

                Func<object, string> sDate = o => { 
                    if (o == null || o == DBNull.Value) return "null";
                    if (o is DateTime) return "\"" + ((DateTime)o).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\"";
                    string d = o.ToString();
                    if (d.Length < 10) return "null"; 
                    DateTime parsed;
                    if (DateTime.TryParse(d, out parsed)) {
                        return "\"" + parsed.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\"";
                    }
                    return "\"" + d.Replace(" ", "T") + "Z\""; 
                };

                using (var conn = new SQLiteConnection(connectionString))
                {
                    conn.Open();

                    SyncTableToOracle(conn, "riplog", apexBaseUrl + "/riplog/", machineName, today, reader => {
                        string estado = reader["State"].ToString();
                        double ancho = Convert.ToDouble(reader["Width"] == DBNull.Value ? 0 : reader["Width"]);
                        double largo = Convert.ToDouble(reader["Length"] == DBNull.Value ? 0 : reader["Length"]);
                        int copias = 1; try { copias = Convert.ToInt32(reader["Copias"]); } catch {}
                        
                        if (estado.ToUpper() == "PRINT") {
                            ancho = 0; largo = 0;
                        } else {
                            ancho = ancho / 10.0;
                            largo = largo / 10.0;
                        }

                        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{{\"FILENAME\":\"{0}\",\"ESTADO\":\"{1}\",\"FECHA_HORA\":{2},\"ANCHO\":{3:0.##},\"LARGO\":{4:0.##},\"COPIAS\":{5},\"MAQUINA_NOMBRE\":\"{6}\",\"DETALLE_ID\":0}}",
                            HttpUtility.JavaScriptStringEncode(reader["FileName"].ToString()),
                            HttpUtility.JavaScriptStringEncode(estado),
                            sDate(reader["StartTime"]),
                            ancho, largo,
                            copias,
                            HttpUtility.JavaScriptStringEncode(machineName)
                        );
                    });

                    SyncTableToOracle(conn, "historialtf", apexBaseUrl + "/historial_tf/", machineName, today, reader => {
                        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{{\"JOB_UID\":\"{0}\",\"JOB_NAME\":\"{1}\",\"START_TIME\":{2},\"END_TIME\":{3},\"ANCHO\":{4},\"LARGO\":{5},\"MODO\":\"{6}\",\"COPIAS_REQUERIDAS\":{7},\"COPIAS_COMPLETADAS\":{8},\"PRODUCCION_PORCENTAJE\":{9},\"VELOCIDAD_M2H\":0,\"IMAGEN_URL\":\"{10}\",\"MAQUINA_NOMBRE\":\"{11}\",\"RIPLOG_ID\":0,\"DETALLE_ID\":0,\"FECHA_PRT\":{12}}}",
                            HttpUtility.JavaScriptStringEncode(reader["Id"].ToString()),
                            HttpUtility.JavaScriptStringEncode(reader["JobName"].ToString()),
                            sDate(reader["StartTime"]), sDate(reader["EndTime"]),
                            reader["Width"], reader["Length"],
                            HttpUtility.JavaScriptStringEncode(reader["Mode"].ToString()),
                            Math.Max(1, Convert.ToInt32(reader["Completed"])), 
                            reader["Completed"], reader["ProductionRatio"],
                            HttpUtility.JavaScriptStringEncode(reader["LocalImagePath"].ToString()), HttpUtility.JavaScriptStringEncode(machineName), sDate(reader["FechaPrt"])
                        );
                    });

                    SyncTableToOracle(conn, "logtxt", apexBaseUrl + "/logs_txt/", machineName, today, reader => {
                        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{{\"JOB_NAME\":\"{0}\",\"START_TIME\":{1},\"END_TIME\":{2},\"ANCHO\":{3},\"LARGO\":{4},\"MODO\":\"{5}\",\"COPIAS\":{6},\"COPIAS_COMPLETADAS\":{7},\"PRODUCCION_PORCENTAJE\":{8},\"TOTAL_PASS\":{9},\"MAX_PASS\":{10},\"MAQUINA_NOMBRE\":\"{11}\",\"RIPLOG_ID\":0,\"DETALLE_ID\":0,\"FECHA_PRT\":{12}}}",
                            HttpUtility.JavaScriptStringEncode(reader["JobName"].ToString()),
                            sDate(reader["StartTime"]), sDate(reader["EndTime"]),
                            reader["Width"], reader["Length"],
                            HttpUtility.JavaScriptStringEncode(reader["Mode"].ToString()),
                            reader["Copies"], reader["Completed"],
                            reader["ProductionRatio"],
                            reader["TotalPass"], reader["MaxPass"], HttpUtility.JavaScriptStringEncode(machineName), sDate(reader["FechaPrt"])
                        );
                    });
                }
            } catch (Exception ex) {
                Console.WriteLine("SyncOracleApex Master Error: " + ex.Message);
            }
        }

        private static void SyncTableToOracle(SQLiteConnection conn, string tableName, string url, string machineName, string today, Func<SQLiteDataReader, string> jsonBuilder)
        {
            var pendingIds = new List<string>();
            var payloads = new List<string>();

            string sql = "SELECT *, rowid as TrueRowId FROM " + tableName + " WHERE sincronizado = 0 AND (StartTime LIKE '" + today.Replace("-", "/") + "%' OR StartTime LIKE '" + today.Replace("/", "-") + "%') ORDER BY rowid ASC LIMIT 200";
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        pendingIds.Add(reader["Id"].ToString());
                        payloads.Add(jsonBuilder(reader));
                    }
                }
            }

            for (int i = 0; i < payloads.Count; i++)
            {
                bool success = false;
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(payloads[i]);
                    req.ContentLength = data.Length;
                    using (var stream = req.GetRequestStream()) { stream.Write(data, 0, data.Length); }

                    using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    {
                        if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300) success = true;
                    }
                }
                catch (System.Net.WebException wex)
                {
                    if (wex.Response != null) {
                        try {
                            using (var errReader = new StreamReader(wex.Response.GetResponseStream())) {
                                Console.WriteLine("Oracle Sync Error (" + tableName + "): " + errReader.ReadToEnd());
                            }
                        } catch {}
                    }
                }
                catch { }

                if (success)
                {
                    using (var cmdUpdate = new SQLiteCommand("UPDATE " + tableName + " SET sincronizado = 1 WHERE Id = @Id", conn))
                    {
                        cmdUpdate.Parameters.AddWithValue("@Id", pendingIds[i]);
                        cmdUpdate.ExecuteNonQuery();
                    }
                }
            }
        }

        public static void SyncRipLog(string ripLogDir)
        {
            string ripLogPath = Path.Combine(ripLogDir, "RIPLOG.HTML");
            if (!File.Exists(ripLogPath)) return;

            // Para no parsear todo el archivo cada minuto (puede ser gigante),
            // guardamos el # de lineas leidas en un archivito config interno si queremos,
            // pero usar INSERT OR IGNORE con Id unico (FileName+StartTime) es ultra rapido tmb.
            
            var lines = new List<string>();
            try {
                using (var fs = new FileStream(ripLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.Default)) {
                    string l;
                    while ((l = sr.ReadLine()) != null) lines.Add(l);
                }
                
                // MÓDULO DE SEGURIDAD PARA BASUREROS GIGANTES (El usuario generó un riplog de 107MB)
                // Usar Regex en un string de 100 millones de caracteres colapsará el Runtime.
                if (lines.Count > 10000) {
                    lines = lines.GetRange(lines.Count - 10000, 10000);
                }
            } catch { return; }

            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    using (var cmd = new SQLiteCommand("INSERT OR IGNORE INTO riplog (Id, FileName, State, StartTime, Width, Length, Copias, SourceFile) VALUES (@Id, @FileName, @State, @StartTime, @Width, @Length, @Copias, @SourceFile)", conn, transaction))
                    {
                        cmd.Parameters.Add(new SQLiteParameter("@Id"));
                        cmd.Parameters.Add(new SQLiteParameter("@FileName"));
                        cmd.Parameters.Add(new SQLiteParameter("@State"));
                        cmd.Parameters.Add(new SQLiteParameter("@StartTime"));
                        cmd.Parameters.Add(new SQLiteParameter("@Width"));
                        cmd.Parameters.Add(new SQLiteParameter("@Length"));
                        cmd.Parameters.Add(new SQLiteParameter("@Copias"));
                        cmd.Parameters.Add(new SQLiteParameter("@SourceFile"));

                        string fullHtml = string.Join("\n", lines);
                        MatchCollection tables = Regex.Matches(fullHtml, @"<TABLE[^>]*>(.*?)</TABLE>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        
                        foreach (Match tbl in tables)
                        {
                            string tContent = tbl.Groups[1].Value;
                            
                            string actionType = null;
                            if (tContent.IndexOf("empezar trabajo de rip", StringComparison.OrdinalIgnoreCase) >= 0) actionType = "RIP";
                            if (tContent.IndexOf("iniciar impresi", StringComparison.OrdinalIgnoreCase) >= 0) actionType = "PRINT";

                            if (actionType == null) continue;

                            MatchCollection rows = Regex.Matches(tContent, @"<TR>(.*?)</TR>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                            
                            var jobData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            
                            foreach (Match r in rows)
                            {
                                string rContent = r.Groups[1].Value;
                                Match thMatch = Regex.Match(rContent, @"<TH[^>]*>\s*(?:&nbsp;)*\s*(.*?)\s*</TH>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                                Match tdMatch = Regex.Match(rContent, @"<TD[^>]*>\s*(?:&nbsp;)*\s*(.*?)\s*(?:&nbsp;)*\s*</TD>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                                
                                if (thMatch.Success && tdMatch.Success)
                                {
                                    string key = Regex.Replace(thMatch.Groups[1].Value, "<.*?>", "").Replace("&nbsp;", "").Trim();
                                    string val = Regex.Replace(tdMatch.Groups[1].Value, "<.*?>", "").Replace("&nbsp;", "").Trim();
                                    if (key.EndsWith(":")) key = key.Substring(0, key.Length - 1).Trim();
                                    jobData[key] = val;
                                }
                            }
                            
                            string fileCell = "";
                            string dateRipCell = "";
                            string datePrintCell = "";
                            string dimCell = "";
                            string impresoraCell = "";
                            string copiasCell = "1";
                            
                            foreach(var k in jobData.Keys) {
                                if (k.StartsWith("Archivo", StringComparison.OrdinalIgnoreCase)) fileCell = jobData[k];
                                else if (k.StartsWith("Dimens", StringComparison.OrdinalIgnoreCase)) dimCell = jobData[k];
                                else if (k.IndexOf("copias", StringComparison.OrdinalIgnoreCase) >= 0) copiasCell = jobData[k];
                                else if (k.StartsWith("Impresora", StringComparison.OrdinalIgnoreCase)) impresoraCell = jobData[k];
                                else if (k.IndexOf("inicio de RIP", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("inicio de la salida", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("envío", StringComparison.OrdinalIgnoreCase) >= 0) dateRipCell = jobData[k];
                                else if (k.IndexOf("finalizaci", StringComparison.OrdinalIgnoreCase) >= 0) datePrintCell = jobData[k];
                                else if (k.IndexOf("copias", StringComparison.OrdinalIgnoreCase) >= 0) copiasCell = jobData[k];
                            }

                            if (!string.IsNullOrEmpty(fileCell))
                            {
                                string state = actionType;
                                string cleanFileName = Path.GetFileName(fileCell);

                                bool isValid = false;
                                if (state == "RIP") {
                                    if (!string.IsNullOrEmpty(impresoraCell) && !cleanFileName.ToLower().Contains("tiff2eps")) {
                                        isValid = true;
                                    }
                                } else if (state == "PRINT") {
                                    isValid = true;
                                }

                                if (isValid) {
                                    string targetDateCell = state == "RIP" ? dateRipCell : datePrintCell;
                                    if (string.IsNullOrEmpty(targetDateCell) && state == "PRINT") targetDateCell = dateRipCell;

                                    if(string.IsNullOrEmpty(targetDateCell)) continue;

                                    DateTime? parsedDate = null;
                                    try {
                                        string[] pDate = targetDateCell.Split(' ');
                                        if(pDate.Length >= 2) {
                                            string[] t = pDate[0].Split(':');
                                            string[] d = pDate[1].Split('/');
                                            if (t.Length==3 && d.Length==3) {
                                                parsedDate = new DateTime(int.Parse(d[2]), int.Parse(d[1]), int.Parse(d[0]), int.Parse(t[0]), int.Parse(t[1]), int.Parse(t[2]));
                                            }
                                        }
                                    } catch {}

                                    if (parsedDate != null && !string.IsNullOrEmpty(cleanFileName))
                                    {
                                        float w = 0f, l = 0f;
                                        string[] pDims = dimCell.Replace("cm", "").Replace("mm", "").Trim().Split('x');
                                        if (pDims.Length == 2)
                                        {
                                            float.TryParse(pDims[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out w);
                                            float.TryParse(pDims[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out l);
                                            if (dimCell.Contains("cm")) { w *= 10; l *= 10; }
                                        }

                                        string sqliteDate = parsedDate.Value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                                        string id = state + "_" + cleanFileName + "_" + sqliteDate.Replace("-", "").Replace(":", "").Replace(" ", "");

                                        cmd.Parameters["@Id"].Value = id;
                                        cmd.Parameters["@FileName"].Value = cleanFileName;
                                        cmd.Parameters["@State"].Value = state;
                                        cmd.Parameters["@StartTime"].Value = sqliteDate;
                                        cmd.Parameters["@Width"].Value = w;
                                        cmd.Parameters["@Length"].Value = l;
                                        int copies = 1; int.TryParse(copiasCell, out copies); cmd.Parameters["@Copias"].Value = copies;
                                        cmd.Parameters["@SourceFile"].Value = "RIPLOG.HTML";
                                        cmd.ExecuteNonQuery();
                        try { new SQLiteCommand("ALTER TABLE riplog ADD COLUMN Copias INTEGER DEFAULT 1", conn).ExecuteNonQuery(); } catch { }
                // Módulo 11: Saneamiento retroactivo de cadenas vacías que corrompen el ADO.NET DateTime parser
                try { new SQLiteCommand("UPDATE historialtf SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                try { new SQLiteCommand("UPDATE logtxt SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                // Saneamiento Nuclear de DATETIMEs
                string[] nuclearCmds = {
                    "UPDATE riplog SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE historialtf SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';",
                    "UPDATE logtxt SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE logtxt SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE logtxt SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';"
                };
                foreach(string nsql in nuclearCmds) {
                    try { new System.Data.SQLite.SQLiteCommand(nsql, conn).ExecuteNonQuery(); } catch {}
                }
        
        
                                    }
                                }
                            }
                        }
                    }
                    transaction.Commit();
                }
            }
        }


        public static void SyncHistorialTf(string tfDir)
        {
            string[] tfFiles = { "printTask.tf", "recordTask.tf", "HistoryTask.tf", "fileTask.tf" };
            var allTfJobs = new List<PrintedJob>();

            foreach (string file in tfFiles)
            {
                string path = Path.Combine(tfDir, file);
                if (File.Exists(path))
                {
                    allTfJobs.AddRange(Program.ParseHistoryTask(path));
                }
            }

            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    using (var cmd = new SQLiteCommand("INSERT OR IGNORE INTO historialtf (Id, JobName, StartTime, EndTime, Width, Length, Mode, Completed, ProductionRatio, LocalImagePath, FechaPrt) VALUES (@Id, @JobName, @StartTime, @EndTime, @Width, @Length, @Mode, @Completed, @ProductionRatio, @LocalImagePath, @FechaPrt)", conn, transaction))
                    {
                        cmd.Parameters.Add(new SQLiteParameter("@Id"));
                        cmd.Parameters.Add(new SQLiteParameter("@JobName"));
                        cmd.Parameters.Add(new SQLiteParameter("@StartTime"));
                        cmd.Parameters.Add(new SQLiteParameter("@EndTime"));
                        cmd.Parameters.Add(new SQLiteParameter("@Width"));
                        cmd.Parameters.Add(new SQLiteParameter("@Length"));
                        cmd.Parameters.Add(new SQLiteParameter("@Mode"));
                        cmd.Parameters.Add(new SQLiteParameter("@Completed"));
                        cmd.Parameters.Add(new SQLiteParameter("@ProductionRatio"));
                        cmd.Parameters.Add(new SQLiteParameter("@LocalImagePath"));
                        cmd.Parameters.Add(new SQLiteParameter("@FechaPrt"));

                        foreach (var job in allTfJobs)
                        {
                            if (string.IsNullOrEmpty(job.Name) || job.StartTime == "-") continue;

                            string id = job.Name + "_" + job.StartTime.Replace("/", "").Replace(":", "").Replace(" ", "");
                            string localImg = "";

                            if (!string.IsNullOrEmpty(job.BmpPath) && File.Exists(job.BmpPath))
                            {
                                string extension = ".jpg";
                                string uniqueName = Guid.NewGuid().ToString("N") + extension;
                                string destPath = Path.Combine(imgCacheDir, uniqueName);
                                
                                try
                                {
                                    // Convierte BMP a JPG 1:1 en disco usando System.Drawing
                                    using (System.Drawing.Image img = System.Drawing.Image.FromFile(job.BmpPath))
                                    {
                                        img.Save(destPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                                    }
                                    localImg = "/img_cache/" + uniqueName;
                                }
                                catch { }
                            }

                            // Reformatear fecha de "DD/MM/YYYY HH:mm:ss" a "YYYY-MM-DD HH:mm:ss" para SQLite sorting
                            string sqliteStart = FormatDateForSqlite(job.StartTime);
                            string sqliteEnd = FormatDateForSqlite(job.EndTime);

                            float w = 0, l = 0;
                            float.TryParse(job.Width, NumberStyles.Any, CultureInfo.InvariantCulture, out w);
                            float.TryParse(job.Length, NumberStyles.Any, CultureInfo.InvariantCulture, out l);

                            cmd.Parameters["@Id"].Value = id;
                            cmd.Parameters["@JobName"].Value = job.Name;
                            cmd.Parameters["@StartTime"].Value = sqliteStart;
                            cmd.Parameters["@EndTime"].Value = sqliteEnd;
                            cmd.Parameters["@Width"].Value = w;
                            cmd.Parameters["@Length"].Value = l;
                            cmd.Parameters["@Mode"].Value = job.Mode ?? "";
                            cmd.Parameters["@Completed"].Value = job.Completed;
                            cmd.Parameters["@ProductionRatio"].Value = job.ProductionRatio;
                            string fechaPrt = null;
                            try {
                                string prtPathTf = string.IsNullOrEmpty(job.PrtPath) ? Path.Combine(tfDir, Path.GetFileNameWithoutExtension(job.Name) + ".prt") : job.PrtPath;
                                if (File.Exists(prtPathTf)) { fechaPrt = new FileInfo(prtPathTf).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture); }
                            } catch {}
                            cmd.Parameters["@LocalImagePath"].Value = localImg;
                            cmd.Parameters["@FechaPrt"].Value = string.IsNullOrWhiteSpace(fechaPrt) ? (object)DBNull.Value : fechaPrt;
                            
                            cmd.ExecuteNonQuery();
                        try { new SQLiteCommand("ALTER TABLE riplog ADD COLUMN Copias INTEGER DEFAULT 1", conn).ExecuteNonQuery(); } catch { }
                // Módulo 11: Saneamiento retroactivo de cadenas vacías que corrompen el ADO.NET DateTime parser
                try { new SQLiteCommand("UPDATE historialtf SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                try { new SQLiteCommand("UPDATE logtxt SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                // Saneamiento Nuclear de DATETIMEs
                string[] nuclearCmds = {
                    "UPDATE riplog SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE historialtf SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';",
                    "UPDATE logtxt SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE logtxt SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE logtxt SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';"
                };
                foreach(string nsql in nuclearCmds) {
                    try { new System.Data.SQLite.SQLiteCommand(nsql, conn).ExecuteNonQuery(); } catch {}
                }
        
        
                        }
                    }
                    transaction.Commit();
                }
            }
        }
        
        private static string FormatDateForSqlite(string frontendDate)
        {
            if (frontendDate == "-" || string.IsNullOrEmpty(frontendDate)) return null;
            try
            {
                DateTime dt;
                string[] formats = { "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss", "dd-MM-yyyy HH:mm:ss" };
                if (DateTime.TryParseExact(frontendDate, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                {
                    return dt.ToString("yyyy-MM-dd HH:mm:ss");
                }
                if (DateTime.TryParse(frontendDate, out dt)) 
                {
                    return dt.ToString("yyyy-MM-dd HH:mm:ss");
                }
                return frontendDate.Replace("/", "-");
            }
            catch { return null; }
        }

        public static void SyncLogTxt(string logTxtDir, string tfDir)
        {
            var rawJobs = Program.ParseLogTxt(logTxtDir);
            
            using (var conn = new SQLiteConnection(connectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    using (var cmd = new SQLiteCommand("INSERT OR IGNORE INTO logtxt (Id, JobName, StartTime, EndTime, Width, Length, Mode, Copies, Completed, ProductionRatio, TotalPass, MaxPass, FechaPrt) VALUES (@Id, @JobName, @StartTime, @EndTime, @Width, @Length, @Mode, @Copies, @Completed, @ProductionRatio, @TotalPass, @MaxPass, @FechaPrt)", conn, transaction))
                    {
                        cmd.Parameters.Add(new SQLiteParameter("@Id"));
                        cmd.Parameters.Add(new SQLiteParameter("@JobName"));
                        cmd.Parameters.Add(new SQLiteParameter("@StartTime"));
                        cmd.Parameters.Add(new SQLiteParameter("@EndTime"));
                        cmd.Parameters.Add(new SQLiteParameter("@Width"));
                        cmd.Parameters.Add(new SQLiteParameter("@Length"));
                        cmd.Parameters.Add(new SQLiteParameter("@Mode"));
                        cmd.Parameters.Add(new SQLiteParameter("@Copies"));
                        cmd.Parameters.Add(new SQLiteParameter("@Completed"));
                        cmd.Parameters.Add(new SQLiteParameter("@ProductionRatio"));
                        cmd.Parameters.Add(new SQLiteParameter("@TotalPass"));
                        cmd.Parameters.Add(new SQLiteParameter("@MaxPass"));
                        cmd.Parameters.Add(new SQLiteParameter("@FechaPrt"));

                        foreach (var job in rawJobs)
                        {
                            if (string.IsNullOrEmpty(job.Name) || job.StartTime == "-") continue;

                            string id = job.Name + "_" + job.StartTime.Replace("/", "").Replace(":", "").Replace(" ", "");

                            float w = 0, l = 0;
                            float.TryParse(job.Width, NumberStyles.Any, CultureInfo.InvariantCulture, out w);
                            float.TryParse(job.Length, NumberStyles.Any, CultureInfo.InvariantCulture, out l);

                            string sqliteStart = FormatDateForSqlite(job.StartTime);
                            string sqliteEnd = FormatDateForSqlite(job.EndTime);

                            cmd.Parameters["@Id"].Value = id;
                            cmd.Parameters["@JobName"].Value = job.Name;
                            cmd.Parameters["@StartTime"].Value = sqliteStart;
                            cmd.Parameters["@EndTime"].Value = sqliteEnd;
                            cmd.Parameters["@Width"].Value = w;
                            cmd.Parameters["@Length"].Value = l;
                            cmd.Parameters["@Mode"].Value = job.Mode ?? "";
                            cmd.Parameters["@Copies"].Value = job.RequiredCopies;
                            cmd.Parameters["@Completed"].Value = job.Completed;
                            cmd.Parameters["@ProductionRatio"].Value = job.ProductionRatio;
                            cmd.Parameters["@TotalPass"].Value = job.SpeedM2h; // We ironically used SpeedM2h to carry TotalPass outward in the Parser
                            string fechaPrtTxt = null;
                              try {
                                  using (var subcmd = new System.Data.SQLite.SQLiteCommand("SELECT FechaPrt FROM historialtf WHERE JobName LIKE @jn AND FechaPrt IS NOT NULL LIMIT 1", conn, transaction)) {
                                      subcmd.Parameters.AddWithValue("@jn", "%" + System.IO.Path.GetFileNameWithoutExtension(job.Name) + "%");
                                      var result = subcmd.ExecuteScalar();
                                      if (result != null && result != DBNull.Value) {
                                          fechaPrtTxt = result.ToString();
                                      }
                                  }
                              } catch {}
                              
                              cmd.Parameters["@MaxPass"].Value = job.Passes;
                              cmd.Parameters["@FechaPrt"].Value = string.IsNullOrWhiteSpace(fechaPrtTxt) ? (object)DBNull.Value : fechaPrtTxt;
                              // Sustituido

                            cmd.ExecuteNonQuery();
                        try { new SQLiteCommand("ALTER TABLE riplog ADD COLUMN Copias INTEGER DEFAULT 1", conn).ExecuteNonQuery(); } catch { }
                // Módulo 11: Saneamiento retroactivo de cadenas vacías que corrompen el ADO.NET DateTime parser
                try { new SQLiteCommand("UPDATE historialtf SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                try { new SQLiteCommand("UPDATE logtxt SET FechaPrt = NULL WHERE FechaPrt = ''", conn).ExecuteNonQuery(); } catch {}
                // Saneamiento Nuclear de DATETIMEs
                string[] nuclearCmds = {
                    "UPDATE riplog SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE historialtf SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE historialtf SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';",
                    "UPDATE logtxt SET StartTime = NULL WHERE length(StartTime) < 10 OR StartTime NOT LIKE '20%';",
                    "UPDATE logtxt SET EndTime = NULL WHERE length(EndTime) < 10 OR EndTime NOT LIKE '20%';",
                    "UPDATE logtxt SET FechaPrt = NULL WHERE length(FechaPrt) < 10 OR FechaPrt NOT LIKE '20%';"
                };
                foreach(string nsql in nuclearCmds) {
                    try { new System.Data.SQLite.SQLiteCommand(nsql, conn).ExecuteNonQuery(); } catch {}
                }
        
        
                        }
                    }
                    transaction.Commit();
                }
            }
        }
    }
}

