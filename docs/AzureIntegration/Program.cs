using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Net.Mail;

namespace GetBuildNumberIfPRChange
{
    class Program
    {

        public static List<string> guidComplete = new List<string>();
        public static string ps_GetSPBuilds = @"c:\Tool\GetBuildSP.ps1";
        public static string ps_GetOMIBuilds = @"c:\Tool\GetBuildOMI.ps1";
        public static string spList = @"c:\Tool\SP.csv";
        public static string vobList = @"c:\Tool\vobs.csv";
        public static List<vobdetails> Repos = new List<vobdetails>();
        public static string spBuild;
        public static int spBuildLog;
        public static ViewChangeDetails[] GetChangesFromOMIVOB(string buildId, string vobName)
        {
            string getChanges = GetBuildChangesOMI(buildId);
            if (vobName == "AppServer.SysObject")
                vobName = "AppServer.AASysObjects";
            getChanges = getChanges.Replace("\r\n\r\n", "~");
            string[] viewChanges = getChanges.Split('~');
            ViewChangeDetails[] chg1 = new ViewChangeDetails[viewChanges.Length];
            for (int i = 0; i < viewChanges.Length; i++)
            {
                if (viewChanges[i] == "") break;
                viewChanges[i] = viewChanges[i].Replace("\r\n", "~");
                string[] testDetails = viewChanges[i].Split('~');

                chg1[i].id = "https://dev.azure.com/AVEVA-VSTS/AppServer%20OMI/_git/" + vobName + "/commit/" + testDetails[0].Substring(testDetails[0].IndexOf("ID: ") + 4);
                chg1[i].name = testDetails[1].Substring(testDetails[1].IndexOf("Message: ") + 9);
                chg1[i].author = testDetails[2].Substring(testDetails[2].IndexOf("Author: ") + 8);
                chg1[i].date = testDetails[3].Substring(testDetails[3].IndexOf("Date: ") + 6);

            }
            return chg1;
        }
        public static ViewChangeDetails[] GetChangesFromVOB(string buildId)
        {
            string getChanges = GetBuildChanges(buildId);
            getChanges = getChanges.Replace("\r\n\r\n", "~");
            string[] viewChanges = getChanges.Split('~');
            ViewChangeDetails[] chg1 = new ViewChangeDetails[viewChanges.Length];
            for (int i = 0; i < viewChanges.Length; i++)
            {
                if (viewChanges[i] == "") break;
                viewChanges[i] = viewChanges[i].Replace("\r\n", "~");
                string[] testDetails = viewChanges[i].Split('~');

                chg1[i].id = "https://dev.azure.com/AVEVA-VSTS/System%20Platform/_git/AASystemPlatformProduct/commit/" + testDetails[0].Substring(testDetails[0].IndexOf("ID: ") + 4);
                chg1[i].name = testDetails[1].Substring(testDetails[1].IndexOf("Message: ") + 9);
                chg1[i].author = testDetails[2].Substring(testDetails[2].IndexOf("Author: ") + 8);
                chg1[i].date = testDetails[3].Substring(testDetails[3].IndexOf("Date: ") + 6);

            }
            return chg1;
        }

        public struct vobdetails
        {
            public string vobname;
            public string buildid;
            public string buildnumber;
            public string buildStatus;
            public string changeSetDescript;
            public string defid;
            public string logid;
            public string prnumber;
            public string buildtype;
            public Dictionary<string, string> othervobsConsumed;
            public ViewChangeDetails[] viewChangesforBuild;

        }

        public struct StatusOfBuild
        {
            public string buildId;
            public string buildNumber;
            public string prNumber;
            public string vobname;
            public string buildStatus;
            public string anyChange;
        }



        public static DataTable dtVOBs = new DataTable();
        public static string spBuildName = "SP"; //"SP2023R2-P01";
        public static string spdefid = "19264";
        public static string magellanName = "2023R2SP1";  //"2023R2P01";

        public static DataTable buildDefIds = new DataTable();
        public static string buildComplete;
        public struct ViewChangeDetails
        {
            public string id;
            public string name;
            public string author;
            public string date;
        }
        public static string buildVersion = "";
        static void Main(string[] args)
        {
            string lines = "";
            if (args.Length == 0)
            {
                Console.WriteLine(" Build number should be provided");
                return;
            }


            string buildNumber = args[0].Trim();
            if (buildNumber.Contains("SP-2023-R2-SP2"))
            {
                spdefid = "23200";
                spBuildLog = 9;
            }
            else
            {
                spdefid = "19264";
                spBuildLog = 24;
            }

            if (args.Length >= 2)
                magellanName = args[1].Trim();

            buildComplete = buildNumber;

            buildDefIds = GetDataTableFromCsv(spList, true);
            for (int i = 0; i < buildDefIds.Rows.Count; i++)
            {
                if (buildNumber.Contains(buildDefIds.Rows[i][0].ToString()))
                {
                    spBuildName = buildDefIds.Rows[i][0].ToString();
                    spdefid = buildDefIds.Rows[i][1].ToString();
                }

            }
            buildNumber = buildNumber.Substring(buildNumber.IndexOf("_") + 1);
            spBuild = buildNumber;
            dtVOBs = GetDataTableFromCsv(vobList, true);
            string output = "", error = "";
            if (File.Exists(@"C:\logs\" + spBuild + @"\log" + buildComplete + ".html"))
                File.Delete(@"C:\logs\" + spBuild + @"\log" + buildComplete + ".html");

            //get 20 SP builds
            GetBuildsFromVOB(Convert.ToInt64(spdefid), ps_GetSPBuilds, out output, out error);


            //retrieve builds and take the old build and get logs
            StatusOfBuild buildCurrent = new StatusOfBuild();
            StatusOfBuild buildOld = new StatusOfBuild();

          
                RetrieveBuildIdsForSP(buildNumber, output, out buildCurrent, out buildOld);

            ViewChangeDetails[] chg1 = GetChangesFromVOB(buildCurrent.buildId);
    
            string getMsg = GetChangeSetDescriptionSP(buildCurrent.buildId);

            vobdetails vob1 = new vobdetails();
            vob1.buildnumber = buildNumber;
            vob1.viewChangesforBuild = chg1;
            vob1.buildid = buildCurrent.buildId;
            vob1.buildStatus = buildCurrent.buildStatus;
            //if (vob1.buildStatus !=null && vob1.buildStatus.ToLower() == "Failed".ToLower())
            //{
            //    return;
            //}
            if (buildCurrent.prNumber != null && buildCurrent.vobname != null)
            {
                vob1.defid = spdefid;
                vob1.prnumber = "https://dev.azure.com/AVEVA-VSTS/System%20Platform/_git/AASystemPlatformProduct/commit/" + buildCurrent.prNumber.ToString().Replace("\"", "");
            }
            //  
            vob1.vobname = buildCurrent.vobname.Replace("\"", "");
            vob1.changeSetDescript = getMsg;
            vob1.buildtype = "sp";
            bool dataExist = false;
            GetLogForSP(buildCurrent.buildId);

            GetLogForSP(buildOld.buildId);
            Dictionary<string, string> othervob = new Dictionary<string, string>();
            if (File.Exists(@"C:\logs\" + spBuild + @"\" + buildCurrent.buildId + ".csv") || File.Exists(@"C:\logs\" + spBuild + @"\" + buildOld.buildId + ".csv"))
            {
                List<vobdetails> productId1 = CompareBuildsForDiff(@"C:\logs\" + spBuild + @"\" + buildCurrent.buildId + ".csv", @"C:\logs\" + spBuild + @"\" + buildOld.buildId + ".csv", buildCurrent.buildId, buildComplete, out othervob);
                vob1.othervobsConsumed = othervob;
                Repos.Add(vob1);
           //     GenerateHTML();

                //if (productId1.Count >= 1)
                {
                    for (int k = 0; k < productId1.Count; k++)
                    {
                        if (!string.IsNullOrEmpty(productId1[k].defid))
                        { 
                        GetBuildsFromVOB(Convert.ToInt64(productId1[k].defid), ps_GetOMIBuilds, out output, out error);
                    guidComplete = RetrieveBuildIdsForOMIMagellan(productId1[k].vobname, productId1[k].buildnumber, productId1[k].logid, output, out buildOld, out buildCurrent);
                  
                    getMsg = GetChangeSetDescription(buildCurrent.buildId);

                    //      GetChangeSetDescription(buildOld.buildId, out changes1);
                    vob1 = new vobdetails();
                    vob1.buildnumber = buildCurrent.buildNumber;
                    vob1.buildid = buildCurrent.buildId;
                    vob1.buildStatus = buildCurrent.buildStatus;
                    vob1.defid = productId1[k].defid;
                    vob1.vobname = buildCurrent.vobname.Replace("\"", "");
                    chg1 = GetChangesFromOMIVOB(buildCurrent.buildId, vob1.vobname);
                    vob1.viewChangesforBuild = chg1;
                    vob1.changeSetDescript = getMsg;
                    vob1.prnumber = "https://dev.azure.com/AVEVA-VSTS/AppServer%20OMI/_git/" + buildCurrent.vobname.ToString().Replace("\"", "") + "/commit/" + buildCurrent.prNumber.ToString().Replace("\"", "");

                    vob1.buildtype = "omi";
                    List<vobdetails> productId2 = CompareBuildsForDiff(@"C:\logs\" + spBuild + @"\" + buildCurrent.buildId + ".csv", @"C:\logs\" + spBuild + @"\" + buildOld.buildId + ".csv", buildCurrent, buildOld, productId1[k].vobname, out othervob);
                    vob1.othervobsConsumed = othervob;
                    Repos.Add(vob1);
              //      GenerateHTML();

                        for (int i = 0; i < productId2.Count; i++)
                        {
                            Console.WriteLine(productId2[i].vobname);
                            dataExist = true;
                            GetBuildsFromVOB(Convert.ToInt64(productId2[i].defid), ps_GetOMIBuilds, out output, out error);
                            RetrieveBuildIdsForOMIMagellan(productId2[i].vobname, productId2[i].buildnumber, productId2[i].logid, output, out buildOld, out buildCurrent);

                            getMsg = GetChangeSetDescription(buildCurrent.buildId);

                                if (buildCurrent.buildId != null)
                                {
                                    vob1 = new vobdetails();
                                    vob1.buildnumber = productId2[i].buildnumber;
                                    vob1.buildid = buildCurrent.buildId;
                                    vob1.buildStatus = buildCurrent.buildStatus;
                                    vob1.defid = productId2[i].defid;
                                    if (buildCurrent.prNumber != null && buildCurrent.vobname != null)
                                        vob1.vobname = buildCurrent.vobname.Replace("\"", "");

                                    vob1.changeSetDescript = getMsg;
                                    vob1.buildtype = "omi";

                                    if (buildCurrent.prNumber != null && buildCurrent.vobname != null)
                                    {
                                        string vobname = buildCurrent.vobname.ToString().Replace("\"", "");
                                        if (vob1.vobname == "AppServer.SysObject")
                                            vobname = "AppServer.AASysObjects";

                                        vob1.prnumber = "https://dev.azure.com/AVEVA-VSTS/AppServer%20OMI/_git/" + vobname + "/commit/" + buildCurrent.prNumber.ToString().Replace("\"", "");
                                    }
                                    chg1 = GetChangesFromOMIVOB(buildCurrent.buildId, vob1.vobname);

                                    vob1.viewChangesforBuild = chg1;
                                    //   vob1.regressiontobecovered = productId2[i].regressiontobecovered;
                                    // vob1.use_cases = productId2[i].use_cases;

                                    if ((File.Exists(@"C:\logs\" + spBuild + @"\" + buildCurrent.buildId + ".csv")) && (File.Exists(@"C:\logs\" + spBuild + @"\" + buildOld.buildId + ".csv")))
                                    {
                                        List<vobdetails> productId4 = CompareBuildsForDiff(@"C:\logs\" + spBuild + @"\" + buildCurrent.buildId + ".csv", @"C:\logs\" + spBuild + @"\" + buildOld.buildId + ".csv", buildCurrent.buildId, productId2[i].vobname, out othervob);
                                        if (productId4.Count >= 1)
                                            vob1.othervobsConsumed = productId4[0].othervobsConsumed;
                                        Repos.Add(vob1);


                                    }
                                }
                            }
                        }
                    }
                }
            }

            GenerateHTML();

            if (dataExist)
            {
                lines = File.ReadAllText(@"c:\Tool\Mails1.txt");

                SendMessage(buildComplete, lines);
            }
            else
            {
                lines = File.ReadAllText(@"c:\Tool\Mails.txt");

                SendMessage(buildComplete, lines);
            }

        }

    




        static DataTable GetDataTableFromCsv(string path, bool isFirstRowHeader)
        {
            string header = isFirstRowHeader ? "Yes" : "No";

            string pathOnly = Path.GetDirectoryName(path);
            string fileName = Path.GetFileName(path);

            string sql = @"SELECT * FROM [" + fileName + "]";

            using (OleDbConnection connection = new OleDbConnection(
                      @"Provider=Microsoft.Jet.OLEDB.4.0;Data Source=" + pathOnly +
                      ";Extended Properties=\"Text;HDR=" + header + "\""))
            using (OleDbCommand command = new OleDbCommand(sql, connection))
            using (OleDbDataAdapter adapter = new OleDbDataAdapter(command))
            {
                DataTable dataTable = new DataTable();
                dataTable.Locale = CultureInfo.CurrentCulture;
                adapter.Fill(dataTable);
                return dataTable;
            }
        }
        private static void SendMessage(string contents, string lines)
        {

            string to = lines; ///args[1]; //To address    
            string from = "wwApps@magellandev2000.dev.wonderware.com"; //From address    
            MailMessage message = new MailMessage(from, to);

            if (contents.Contains("Failed"))
                message.Subject = "New Build Generated -" + buildComplete + "-" + contents;
            else
            {
                message.Subject = "New Build Generated -" + buildComplete;
                //      Attachment att = new Attachment(@"c:\logs\" + spBuild + @"\log" + buildComplete + ".html");
                //     message.Attachments.Add(att);
                message.IsBodyHtml = true;
                string body = string.Empty;
                if (File.Exists(@"c:\logs\" + spBuild + @"\" + buildComplete + ".html"))
                {
                    using (StreamReader reader = new StreamReader(@"c:\logs\" + spBuild + @"\" + buildComplete + ".html"))
                    {
                        body = reader.ReadToEnd();
                    }
                }
                //using (StreamReader reader = new StreamReader(@"c:\logs\" + spBuild + @"\log" + buildComplete + ".html"))
                //{
                //    body = body + reader.ReadToEnd();
                //}
                message.Body = body;

            }

            message.BodyEncoding = Encoding.UTF8;
            message.IsBodyHtml = true;
            SmtpClient client = new SmtpClient("smtp");

            System.Net.NetworkCredential basicCredential1 = new
            System.Net.NetworkCredential("wwapps@magellandev2000.dev.wonderware.com", "StoneExpand2025!26");
            client.EnableSsl = false;
            client.UseDefaultCredentials = false;
            client.Credentials = basicCredential1;
            try
            {
                      client.Send(message);
                Console.WriteLine("Mail Sent");
            }

            catch (Exception ex)
            {
                throw ex;
            }

        }
        public static string GetBuildChanges(string buildId)
        {

            string output, errors;
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'C:\Tool\GetBuildChanges.ps1 " + buildId + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();
            output = process.StandardOutput.ReadToEnd();

            errors = process.StandardError.ReadToEnd();

            return output;
        }
        public static string GetBuildChangesOMI(string buildId)
        {
            
            string output, errors;
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'C:\Tool\GetBuildChanges_OMI.ps1 " + buildId + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();
            output = process.StandardOutput.ReadToEnd();

            errors = process.StandardError.ReadToEnd();

            return output;
        }

        public static void GetBuildsFromVOB(Int64 buildDefID, string fileName, out string output, out string errors)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  '" + fileName + "  " + buildDefID + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            output = process.StandardOutput.ReadToEnd();

            errors = process.StandardError.ReadToEnd();
            //      File.WriteAllText(@"c:\statusfile.txt", output);
        }


        public static string GetChangeSetDescription(string runID)
        {
            string logID = "6";
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'C:\Tool\GEtChangeName.ps1 " + runID + " " + logID + " " + spBuild + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();
            string descript = "";

            System.Threading.Thread.Sleep(20000);

            if (File.Exists(@"C:\logs\" + spBuild + @"\CS_" + runID + ".csv"))
            {
                string line;
                // Read the file and display it line by line.
                System.IO.StreamReader file = new System.IO.StreamReader(@"C:\logs\" + spBuild + @"\CS_" + runID + ".csv");
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("HEAD is now at"))
                    {
                        descript = line.Substring(line.LastIndexOf("HEAD is now at") + 23);
                    }

                }
                file.Close();
            }
            return descript;

        }
        //while ((line = file.ReadLine()) != null)
        //{
        //    //if (line.Contains("##[command]git checkout --progress --force"))
        //    //{
        //    //    changeData= line.Substring(line.LastIndexOf("##[command]git checkout --progress --force") + 43);

        //    //}
        //    if (line.Contains("HEAD is now at"))
        //    {
        //        descript = line.Substring(line.LastIndexOf("HEAD is now at") + 23) + "<br>" + descript;
        //        changeData = line.Substring(line.LastIndexOf("HEAD is now at") + 15, 7); // line.Substring(line.LastIndexOf("##[command]git checkout --progress --force") + 43);


        //    }
        //    if (changeData != "" && descript != "")
        //    {
        //        DescriptChanges st = new DescriptChanges();
        //        st.descChange = descript;
        //        st.guidChange = changeData;
        //        changes.Add(st);
        //        descript = ""; changeData = "";
        //    }

        //}
        //file.Close();



        public static string GetChangeSetDescriptionSP(string runID)
        {
            string logID = "5";
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'C:\Tool\GEtChangeNameSP.ps1 " + runID + " " + logID + " " + spBuild + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();
            string descript = "";
            System.Threading.Thread.Sleep(20000);
            if (File.Exists(@"C:\logs\" + spBuild + @"\CS_" + runID + ".csv"))
            {
                string line;
                // Read the file and display it line by line.
                System.IO.StreamReader file = new System.IO.StreamReader(@"C:\logs\" + spBuild + @"\CS_" + runID + ".csv");
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("HEAD is now at"))
                    {
                        descript = line.Substring(line.LastIndexOf("HEAD is now at") + 23);
                    }

                }
                file.Close();

            }
            return descript;
        }


        public static void GetLogForSP(string buildID)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'c:\Tool\GetLogSP.ps1 " + buildID + " " + spBuild + "' "+spBuildLog+"";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            Console.Write(process.StandardOutput.ReadToEnd());

            Console.Write(process.StandardError.ReadToEnd());
        }

        public static StatusOfBuild GetBuildStatusSP(string buildID, string version)
        {
            if (File.Exists(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv"))
                File.Delete(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv");

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'c:\Tool\GetBuildStatusSP.ps1 " + buildID + " " + spBuild + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            Console.Write(process.StandardOutput.ReadToEnd());

            Console.Write(process.StandardError.ReadToEnd());
            StatusOfBuild buildState = new StatusOfBuild();
            if (File.Exists(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv"))
            {
                string line;
                bool firstCond = false, secondCond = false, thirdCond = false;
                // Read the file and display it line by line.
                System.IO.StreamReader file = new System.IO.StreamReader(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv");
                buildState.buildId = buildID;
                buildState.buildStatus = "Failed";
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("\"result\": \"succeeded\""))
                    {
                        firstCond = true;
                    }
                    if (line.Contains("\"status\": \"completed\""))
                    {
                        secondCond = true;
                    }

                    //if (line.Contains("\"sourceBranch\"") && line.Contains(version))
                    //{
                    //    thirdCond = true;
                    //}

                    if (line.Contains("\"name\"") && buildState.vobname == null)
                    {
                        buildState.vobname = line.Substring(line.IndexOf(":") + 2, line.Length - line.IndexOf(":") - 3);
                    }
                    if (line.Contains("\"sourceVersion\"") && buildState.prNumber == null)
                    {
                        buildState.prNumber = line.Substring(line.IndexOf(":") + 2, line.Length - line.IndexOf(":") - 3);

                    }


                }
                file.Close();
                if (firstCond == true && secondCond == true)
                {

                    buildState.buildStatus = "Succeeded";
                }
            }
            return buildState;
        }


        public static StatusOfBuild GetBuildStatusOMIForMagellandev(string buildID, string version)
        {
            if (File.Exists(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv"))
                File.Delete(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv");

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'c:\Tool\GetBuildStatusOMI.ps1 " + buildID + " " + spBuild + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            Console.Write(process.StandardOutput.ReadToEnd());

            Console.Write(process.StandardError.ReadToEnd());
            StatusOfBuild stTest = new StatusOfBuild();
            if (File.Exists(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv"))
            {
                string line;
                bool firstCond = false, secondCond = false, thirdCond = false;
                // Read the file and display it line by line.
                System.IO.StreamReader file = new System.IO.StreamReader(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv");
                stTest.buildId = buildID;
                stTest.buildStatus = "Failed";
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("\"result\": \"succeeded\""))
                    {
                        firstCond = true;
                    }
                    if (line.Contains("\"status\": \"completed\""))
                    {
                        secondCond = true;
                    }
                    if (line.Contains("\"sourceBranch\"") && line.Contains(version))
                    {
                        thirdCond = true;
                    }
                    if (line.Contains("\"name\"") && stTest.vobname == null)
                    {
                        stTest.vobname = line.Substring(line.IndexOf(":") + 2, line.Length - line.IndexOf(":") - 3);
                    }
                    if (line.Contains("\"sourceVersion\"") && stTest.prNumber == null)
                    {
                        stTest.prNumber = line.Substring(line.IndexOf(":") + 2, line.Length - line.IndexOf(":") - 3);

                    }

                }
                file.Close();
                if (firstCond == true && secondCond == true && thirdCond == true)
                {

                    stTest.buildStatus = "Succeeded";
                }
            }
            return stTest;
        }

        public static string GetBuildStatusOMIForOtherVOBs(string buildID, string version)
        {
            if (File.Exists(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv"))
                File.Delete(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv");

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'c:\Tool\GetBuildStatusOMI.ps1 " + buildID + " " + spBuild + "'";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            Console.Write(process.StandardOutput.ReadToEnd());

            Console.Write(process.StandardError.ReadToEnd());

            if (File.Exists(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv"))
            {
                string line;
                string oldbuildVersion = "";
                bool firstCond = false, secondCond = false, thirdCond = false;
                // Read the file and display it line by line.
                System.IO.StreamReader file = new System.IO.StreamReader(@"C:\logs\" + spBuild + @"\Status_" + buildID + ".csv");
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("\"result\": \"succeeded\""))
                    {
                        firstCond = true;
                    }
                    if (line.Contains("\"status\": \"completed\""))
                    {
                        secondCond = true;
                    }
                    if (line.Contains("\"result\": \"succeeded\""))
                    {
                        firstCond = true;
                    }
                    if (line.Contains("\"sourceBranch\"") && line.Contains(version))
                    {
                        thirdCond = true;
                    }
                    if (line.Contains("\"sourceVersion\"") && buildVersion == "")
                    {
                        buildVersion = line.Substring(line.IndexOf(":") + 2, line.Length - line.IndexOf(":") - 3);

                    }
                    if (line.Contains("\"sourceVersion\"") && buildVersion != "")
                    {
                        oldbuildVersion = line.Substring(line.IndexOf(":") + 2, line.Length - line.IndexOf(":") - 3);
                    }

                }
                file.Close();
                if (firstCond && secondCond && thirdCond && buildVersion != oldbuildVersion)
                    return buildID;
                else
                    return "Failed";

            }
            return "Failed";
        }

        public static void GetLogForOMI(string buildID, string logid)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = @"powershell.exe";
            startInfo.Arguments = @"&powershell.exe  'c:\Tool\GetLogOMI.ps1 " + buildID + " " + logid + " " + spBuild + "' ";
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            Console.Write(process.StandardOutput.ReadToEnd());

            Console.Write(process.StandardError.ReadToEnd());



        }

        public static void RetrieveBuildIdsForSP(string buildNumber, string output, out StatusOfBuild buildcurrent, out StatusOfBuild buildold)
        {
            string[] attr1 = output.Split(new char[] { '\r', '\n' });
            int j = 0;
            string[] id;
            string build1 = buildNumber.IndexOf("_") >0? buildNumber.Substring(buildNumber.IndexOf("_") + 1): buildNumber;
            buildold = new StatusOfBuild();
            buildcurrent = new StatusOfBuild();
            string buildId = "";
            for (int i = 0; i < attr1.Length; i++)
            {
                if (attr1[i] != "" && buildId == "" && attr1[i].Contains(build1))
                {
                    id = attr1[i].Split('\t');
                    buildcurrent = GetBuildStatusSP(id[0], spBuildName);
                    buildId = buildcurrent.buildId;
                    buildcurrent.buildNumber = id[1];
                }
                else if (attr1[i] != "" && buildId != "")
                {
                    id = attr1[i].Split('\t');
                    buildold = GetBuildStatusSP(id[0], spBuildName);
                    buildold.buildNumber = id[1];
                    File.AppendAllText(@"c:\statusfile.txt", "SP:" + buildold.buildNumber);
                    File.AppendAllText(@"c:\statusfile.txt", "SP:" + buildold.prNumber);
                    File.AppendAllText(@"c:\statusfile.txt", "SP:" + buildold.vobname);
                    if (buildcurrent.prNumber != buildold.prNumber)
                        break;
                }

            }

        }


        public static List<string> RetrieveBuildIdsForOMIMagellan(string vob, string buildNumber, string logID, string output, out StatusOfBuild buildold, out StatusOfBuild buildcurrent)
        {

            string[] attr1 = output.Split(new char[] { '\r', '\n' });
            string[] id;
            string buildId = "";
            buildold = new StatusOfBuild();
            buildcurrent = new StatusOfBuild();
            List<string> ids = new List<string>();
            //   buildNumber = buildNumber.Substring(0, buildNumber.LastIndexOf('.'));
            for (int i = 0; i < attr1.Length; i++)
            {
                if (attr1[i] != "" && buildId == "" && (attr1[i].Contains(buildNumber) || attr1[i].Contains(buildNumber.Substring(0, buildNumber.Length - buildNumber.IndexOf(".0-") + 3))))
                {
                    id = attr1[i].Split('\t');
                    buildcurrent = GetBuildStatusOMIForMagellandev(id[0], magellanName);
                    buildcurrent.buildNumber = id[1];
            
                    ids.Add(buildcurrent.prNumber);
                    buildId = buildcurrent.buildId;
                }
                else if (attr1[i] != "" && buildId != "")
                {
                    id = attr1[i].Split('\t');
                    buildold = GetBuildStatusOMIForMagellandev(id[0], magellanName);
                    buildold.buildNumber = id[1];
                    ids.Add(buildold.prNumber);
                    File.AppendAllText(@"c:\statusfile.txt", vob + ":" + buildold.buildNumber + "--" + buildold.prNumber + "-" + buildold.vobname + "\\n");
                    if (buildcurrent.prNumber != buildold.prNumber)
                        break;
                }
            }

            if (!string.IsNullOrEmpty(buildcurrent.vobname))
                Console.WriteLine(buildcurrent.vobname);
            //if (!string.IsNullOrEmpty(buildcurrent.vobname) && buildcurrent.vobname.Contains("AppServer.Magellan") && magellanName.Contains("2023R2P01"))
            //    logID = "13";
            //else
            //    logID = "14";
            GetLogForOMI(buildcurrent.buildId, logID);
            GetLogForOMI(buildold.buildId, logID);
            return ids;

        }
        public static void RetrieveBuildIdsForOMIOther(string buildNumber, string logID, string output, out string buildId, out string oldbuildId)
        {
            string[] attr1 = output.Split(new char[] { '\r', '\n' });
            int j = 0;
            string[] id;
            buildId = "";
            //   buildNumber = buildNumber.Substring(0, buildNumber.LastIndexOf('.'));
            oldbuildId = "Failed";
            for (int i = 0; i < attr1.Length; i++)
            {
                if (attr1[i] != "" && buildId == "" && attr1[i].Contains(buildNumber))
                {
                    id = attr1[i].Split('\t');
                    //    buildId = id[0];
                    buildId = GetBuildStatusOMIForOtherVOBs(id[0], magellanName);
                }
                else if (attr1[i] != "" && buildId != "" && oldbuildId == "Failed")
                {
                    id = attr1[i].Split('\t');
                    oldbuildId = GetBuildStatusOMIForOtherVOBs(id[0], magellanName);
                    break;
                    // break;
                }
            }
            if (buildId == "Failed" || oldbuildId == "Failed")

            {
                return;
            }
            else
            {
                GetLogForOMI(buildId, logID);
                GetLogForOMI(oldbuildId, logID);
            }
        }


        public static void GenerateHTML()
        {
            string fileName = @"C:\logs\" + spBuild + @"\" + buildComplete + ".html";
            if (File.Exists(fileName)) File.Delete(fileName);
            using (StreamWriter writer = new StreamWriter(fileName, true))
            {
                writer.WriteLine(" <H1 ALIGN='CENTER' style='color:darkblue;'> List of repos consumed on SP Build " + spBuild + "</h1>");
                writer.WriteLine("<BR><table  align='center' border-color='darkgreen' cellpadding='3' cellspacing='5' style='font-family:Arial;font-size:small;width=90%;border:2px;table-layout:fixed'>");

                writer.WriteLine("<tr bgcolor='lightgreen' >");

                writer.WriteLine("<br><td style='font-weight:bold'>Repo Name</td>");
                writer.WriteLine("<td style='font-weight:bold'>Build Number</td>");
                 writer.WriteLine("<td style='font-weight:bold'> PR Number </td>");
                writer.WriteLine("<td style='font-weight:bold'> Repos Consumed </td>"); 
                 writer.WriteLine(" <td style = 'font-weight:bold' > View Changes </td > </tr> ");
                for (int i = 0; i < Repos.Count; i++)
                {
                    writer.WriteLine("<tr  bgcolor='lightyellow'>");
                    if (Repos[i].buildtype.Contains("sp"))
                        writer.WriteLine("<td><a href=\"https://dev.azure.com/AVEVA-VSTS/System%20Platform/_build?definitionId=" + Repos[i].defid + "&_a=summary\"> " + Repos[i].vobname + "</a></td>");

                    else
                    {


                        writer.WriteLine("<td><a href=\"https://dev.azure.com/AVEVA-VSTS/AppServer%20OMI/_build?definitionId=" + Repos[i].defid + "&_a=summary\"> " + Repos[i].vobname + "</a></td>");
                    }
                    if (Repos[i].buildtype.Contains("sp"))
                        writer.WriteLine("<td><a href=\"https://dev.azure.com/AVEVA-VSTS/System%20Platform/_build/results?buildId="+ Repos[i].buildid + "&view=results\"> " + Repos[i].buildnumber+"</a></td>");

                    else
                        writer.WriteLine("<td><a href=\"https://dev.azure.com/AVEVA-VSTS/AppServer%20OMI/_build/results?buildId="+ Repos[i].buildid+ "&view=results\" > " + Repos[i].buildnumber + " </a></td>");

                    writer.WriteLine("<td><a href='" + Repos[i].prnumber + "'>"+ Repos[i].changeSetDescript+ "</a></td>");

                   

                    writer.WriteLine("<td>");
                    if (Repos[i].othervobsConsumed != null)
                        foreach (var cu in Repos[i].othervobsConsumed)
                        {
                            writer.WriteLine(cu.Key);
                            writer.WriteLine("::");
                            writer.WriteLine(cu.Value);
                            writer.WriteLine("<br>");
                        }
                    writer.WriteLine("</td>");

                    writer.WriteLine("<td>");
                    if (Repos[i].viewChangesforBuild != null)
                        foreach (var cu in Repos[i].viewChangesforBuild)
                        {
                            writer.WriteLine("<a href='" + cu.id + "'> " + cu.name + "</a>");
                            writer.WriteLine("<br>");
                        }
                    writer.WriteLine("</td>");
                    //  writer.WriteLine("<td style='font-weight:bold'>" + Repos[i].use_cases + "</td>");

                    writer.WriteLine("</tr>");
                }
                writer.WriteLine("</table><br><br>");
            }


        }

        public static List<vobdetails> CompareBuildsForDiff(string fileName1, string fileName2, StatusOfBuild buildCurrent, StatusOfBuild buildOld, string buildNumber, out Dictionary<string, string> changed)
        {
            string path = "", value = "";
            string prod = "";
            List<vobdetails> changedvob = new List<vobdetails>();
            var filepath = fileName1; // Habeeb, "Dubai Media City, Dubai"
            Dictionary<string, string> current = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(filepath))
            {

                if (line.Contains("32;1mName"))
                {
                    path = line.Substring(line.IndexOf("[0m") + 4);
                }

                if (line.Contains("32;1mVersion"))
                {
                    value = line.Substring(line.IndexOf("[0m") + 3);
                }
                if (line.Contains(" Name                        : "))
                {
                    path = line.Substring(line.IndexOf(": ") + 60);
                }
                if (line.Contains(" Version                     : "))
                {
                    value = line.Substring(line.IndexOf("                    : ") + 60);
                }
                if (path != "" && value != "")
                {
                    if (!current.ContainsKey(path))
                        current.Add(path, value);
                    path = "";
                    value = "";

                }
            }
            foreach (var cu in current)
            {
                Console.WriteLine(cu.Key + " " + cu.Value);
            }

            path = ""; value = "";
            filepath = fileName2; /// @"C:\Users\leelarani.pasumarthi\Desktop\OUTPUT_Magellan.csv"; // Habeeb, "Dubai Media City, Dubai"
            Dictionary<string, string> oldone = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(filepath))
            {

                if (line.Contains("32;1mName"))
                {
                    path = line.Substring(line.IndexOf("[0m") + 4);
                }

                if (line.Contains("32;1mVersion"))
                {
                    value = line.Substring(line.IndexOf("[0m") + 3);
                }
                if (line.Contains(" Name                        : "))
                {
                    path = line.Substring(line.IndexOf(": ") + 60);
                }
                if (line.Contains(" Version                     : "))
                {
                    value = line.Substring(line.IndexOf("                    : ") + 60);
                }
                try
                {
                    if (path != "" && value != "")
                    {
                        if (!oldone.ContainsKey(path))
                            oldone.Add(path, value);
                        path = "";
                        value = "";

                    }
                }
                catch
                {
                    Console.WriteLine("Duplicate  " + path);
                }
            }
            Random rnd = new Random();
            changed = new Dictionary<string, string>();
            Dictionary<string, string> notchanged = new Dictionary<string, string>();
            string fileName = @"C:\logs\" + spBuild + @"\log" + buildComplete + ".html";

            using (StreamWriter writer = new StreamWriter(fileName, true))
            {

                foreach (var cu in current)
                {
                    string val2;
                    oldone.TryGetValue(cu.Key, out val2);
                    if (val2 != cu.Value)
                    {
                        changed.Add(cu.Key, cu.Value);
                    }
                    else
                        notchanged.Add(cu.Key, cu.Value);
                }
                writer.WriteLine(" <H1 ALIGN='CENTER' style='color:darkblue;'> Changes Done on SP Build " + spBuild + "</h1>");
                writer.WriteLine("<BR><table  align='center' border-color='darkgreen' cellpadding='3' cellspacing='5' style='font-family:Arial;font-size:small;width=90%;border:2px;table-layout:fixed'>");
                writer.WriteLine(" <b>**************************************************************<br>");
                writer.WriteLine("<b>Below VOBS are consumed on build " + buildNumber + "</b><br>");
                writer.WriteLine(" <b>*************************************************************</b><br>");
                foreach (var cu in changed)
                {
                    writer.WriteLine(cu.Key + "   " + cu.Value + "<br>");
                }
                writer.WriteLine("<b>*************************************************************<br>");
                writer.WriteLine("<b>Below VOBS are not consumed on  build " + buildNumber + "</b><br>");
                writer.WriteLine("<b>*************************************************************</b><br>");

                foreach (var cu in notchanged)
                {
                    writer.WriteLine(cu.Key + "   " + cu.Value + "<br>");
                }
                writer.WriteLine("</table> <BR>");
            }

            foreach (var cu in changed)
            {
                for (int i = 0; i < dtVOBs.Rows.Count; i++)
                {
                    if (cu.Key.Contains(dtVOBs.Rows[i][0].ToString()))
                    {
                        vobdetails det = new vobdetails();
                        det.vobname = cu.Key;
                        det.buildid = cu.Value;
                        det.buildnumber = cu.Value.Substring(0, cu.Value.LastIndexOf('.'));
                        det.defid = dtVOBs.Rows[i][1].ToString();
                        det.logid = dtVOBs.Rows[i][2].ToString();
                        det.othervobsConsumed = changed;
                        //      det.use_cases = dtVOBs.Rows[i][4].ToString();
                        det.prnumber = "https://dev.azure.com/AVEVA-VSTS/AppServer%20OMI/_git/" + buildCurrent.vobname + "/commit/" + buildCurrent.prNumber;


                        changedvob.Add(det);

                        //   Repos.Add(det);

                    }


                }
            }

            return changedvob;
        }


        public static List<vobdetails> CompareBuildsForDiff(string fileName1, string fileName2, string buildId, string buildNumber, out Dictionary<string, string> changed)
        {
            string path = "", value = "";
            string prod = "";
            List<vobdetails> changedvob = new List<vobdetails>();
            var filepath = fileName1; // Habeeb, "Dubai Media City, Dubai"
            Dictionary<string, string> current = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(filepath))
            {

                if (line.Contains("32;1mName"))
                {
                    path = line.Substring(line.IndexOf("[0m") + 4);
                }
                if (line.Contains(" Name                        : "))
                {
                    path = line.Substring(line.IndexOf(": ") + 60);
                }
                if (line.Contains(" Version                     : "))
                {
                    value = line.Substring(line.IndexOf("                    : ") + 60);
                }
                if (line.Contains("32;1mVersion"))
                {
                    value = line.Substring(line.IndexOf("[0m") + 3);
                }

                if (path != "" && value != "")
                {
                    if (!current.ContainsKey(path))
                        current.Add(path, value);
                    path = "";
                    value = "";

                }
            }
            foreach (var cu in current)
            {
                Console.WriteLine(cu.Key + " " + cu.Value);
            }

            path = ""; value = "";
            filepath = fileName2; /// @"C:\Users\leelarani.pasumarthi\Desktop\OUTPUT_Magellan.csv"; // Habeeb, "Dubai Media City, Dubai"
            Dictionary<string, string> oldone = new Dictionary<string, string>();
            if(File.Exists(filepath))
            {
                foreach (var line in File.ReadAllLines(filepath))
                {

                    if (line.Contains("32;1mName"))
                    {
                        path = line.Substring(line.IndexOf("[0m") + 4);
                    }

                    if (line.Contains("32;1mVersion"))
                    {
                        value = line.Substring(line.IndexOf("[0m") + 3);
                    }
                    if (line.Contains(" Name                        : "))
                    {
                        path = line.Substring(line.IndexOf(": ") + 60);
                    }
                    if (line.Contains(" Version                     : "))
                    {
                        value = line.Substring(line.IndexOf("                    : ") + 60);
                    }
                    try
                    {
                        if (path != "" && value != "")
                        {
                            if (!oldone.ContainsKey(path))
                                oldone.Add(path, value);
                            path = "";
                            value = "";

                        }
                    }
                    catch
                    {
                        Console.WriteLine("Duplicate  " + path);
                    }
                }
            }
            Random rnd = new Random();
            changed = new Dictionary<string, string>();
            Dictionary<string, string> notchanged = new Dictionary<string, string>();
            string fileName = @"C:\logs\" + spBuild + @"\log" + buildComplete + ".html";

            using (StreamWriter writer = new StreamWriter(fileName, true))
            {

                foreach (var cu in current)
                {
                    string val2;
                    oldone.TryGetValue(cu.Key, out val2);
                    if (val2 != cu.Value)
                    {
                        changed.Add(cu.Key, cu.Value);
                    }
                    else
                        notchanged.Add(cu.Key, cu.Value);
                }

                writer.WriteLine(" <H1 ALIGN='CENTER' style='color:darkblue;'> Changes Done on SP Build " + spBuild + "</h1>");

                writer.WriteLine("<BR><table  align='center' border-color='darkgreen' cellpadding='3' cellspacing='5' style='font-family:Arial;font-size:small;width=90%;border:2px;table-layout:fixed'>");

                if (changed.Count != 0)
                {
                    writer.WriteLine(" <b>**************************************************************<br>");
                    writer.WriteLine(" <b>Below VOBS are consumed on build " + buildNumber + "</b><br>");
                    writer.WriteLine(" <b>*************************************************************</b><br>");
                    foreach (var cu in changed)
                    {
                        writer.WriteLine(cu.Key + "   " + cu.Value + "<br>");
                    }
                }
                if (notchanged.Count != 0)
                {
                    writer.WriteLine("<b>*************************************************************<br>");
                    writer.WriteLine(" <b>Below VOBS are not consumed on  build " + buildNumber + "</b><br>");
                    writer.WriteLine("<b>*************************************************************</b><br>");

                    foreach (var cu in notchanged)
                    {
                        writer.WriteLine(cu.Key + "   " + cu.Value + "<br>");
                    }

                }
                writer.WriteLine("</table> <BR>");
            }

            foreach (var cu in changed)
            {
                for (int i = 0; i < dtVOBs.Rows.Count; i++)
                {
                    if (cu.Key.Contains(dtVOBs.Rows[i][0].ToString()))
                    {
                        vobdetails det = new vobdetails();
                        det.vobname = cu.Key;
                        det.buildid = cu.Value;
                        det.buildnumber = cu.Value.Substring(0, cu.Value.LastIndexOf('.'));
                        det.defid = dtVOBs.Rows[i][1].ToString();
                        det.logid = dtVOBs.Rows[i][2].ToString();
                        det.othervobsConsumed = changed;


                        changedvob.Add(det);

                        //   Repos.Add(det);

                    }


                }
            }

            return changedvob;
        }


    }
}
