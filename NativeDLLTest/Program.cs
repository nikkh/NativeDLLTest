using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NativeDLLTest
{
    class Program
    {
        #region Win32 APIs
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string libname);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]


        private static extern bool FreeLibrary(IntPtr hModule);
        #endregion

        #region delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate bool IsHibernateAllowed();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr NicksIntFunction();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr NicksStringFunction();

        #endregion

        static async Task Main(string[] args)
        {
            Console.WriteLine($"{DateTime.Now.ToString()} Welcome to Nick's DLL Experiments");
            bool downloadDll = false;
            string dllLoadPath = "";
            var configBuilder = new ConfigurationBuilder()
               .SetBasePath(Directory.GetCurrentDirectory())
               .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
               .AddJsonFile("settings.json", optional: true, reloadOnChange: true)
               .AddEnvironmentVariables();
            IConfiguration configuration = configBuilder.Build();
            try
            {
                var storageConnectionString = configuration["StorageConnectionString"];
                var inboundContainerName = configuration["RunContext:InboundContainer"];
                var outboundContainerName = configuration["RunContext:OutboundContainer"];
                var dllName = configuration["RunContext:DLLName"];
                dllLoadPath = dllName;
                var functionName = configuration["RunContext:FunctionName"];
                var inboundBlobName = configuration["RunContext:InboundBlobName"];
                var outboundBlobPrefix = configuration["RunContext:OutboundBlobPrefix"];
                var outboundBlobSuffix = configuration["RunContext:OutboundBlobSuffix"];
                var dllNameInBlobStorage = configuration["RunContext:DllNameInBlobStorage"];
                Console.WriteLine("Configuration:");
                Console.WriteLine($"storage connection string startswith={storageConnectionString.Substring(0, 30)}");
                Console.WriteLine($"inbound container={inboundContainerName}");
                Console.WriteLine($"inbound blob name ={inboundBlobName}");
                Console.WriteLine($"outbound container={outboundContainerName}");
                Console.WriteLine($"outbound blob prefix={outboundBlobPrefix}"); 
                Console.WriteLine($"outbound blob suffix={outboundBlobSuffix}");
                Console.WriteLine();
                Console.WriteLine($"dll name={dllName}");
                Console.WriteLine($"function name={functionName}");
                Console.WriteLine();
                if (!string.IsNullOrEmpty(dllNameInBlobStorage))
                {
                    Console.WriteLine($"There is apparently a dll in {inboundContainerName}. I'll deal with that later!");
                    downloadDll = true;

                }
                // Environment logging
                WriteEnvironmentData();

                // Some stuff with blob storage
                CloudBlobClient blobClient;
                CloudBlobContainer inboundContainer;
                try
                {
                    var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
                    blobClient = storageAccount.CreateCloudBlobClient();
                    inboundContainer = blobClient.GetContainerReference(inboundContainerName);
                    var inboundBlob = inboundContainer.GetBlockBlobReference(inboundBlobName);
                    var inboundBlobContents = await inboundBlob.DownloadTextAsync();
                    var jo = JObject.Parse(inboundBlobContents);
                    // pull the blob SAS Url just for something to do
                    var blobSasUrl = jo["BlobSasUrl"].ToString();
                    JObject body = new JObject(
                        new JProperty("DateTime", DateTime.Now.ToString()),
                        new JProperty("BlobSasStuff",
                            new JObject(
                                new JProperty("BlobSasUrl", blobSasUrl),
                                new JProperty("MeaninglessGuid", Guid.NewGuid().ToString())
                            )
                        ),
                    new JProperty("LastThing", "test"));

                    string outboundBlobName = $"{outboundBlobPrefix}{DateTime.Now.Ticks}{outboundBlobSuffix}";
                    var outboundContainer = blobClient.GetContainerReference(outboundContainerName);
                    var outboundBlob = outboundContainer.GetBlockBlobReference(outboundBlobName);
                    var outboundBlobContents = body.ToString();
                    await outboundBlob.UploadTextAsync(outboundBlobContents);
                }
                catch (Exception e)
                {
                    throw new Exception($"Error processing blob storage {e.Message}", e);
                }

                // some stuff with local storage
                if (downloadDll)
                {
                    Console.WriteLine($"Downloading DLL...");
                    string dllFileName;
                    try
                    {
                        Console.WriteLine($"Current directory={Directory.GetCurrentDirectory()}");
                        var directoryToCreate = $"{Directory.GetCurrentDirectory()}\\dll";
                        Directory.CreateDirectory(directoryToCreate);
                        Console.WriteLine($"directory {directoryToCreate} was created");
                        dllFileName = $"{directoryToCreate}\\{dllNameInBlobStorage}";
                        var dllBlob = inboundContainer.GetBlockBlobReference(dllNameInBlobStorage);
                        await dllBlob.FetchAttributesAsync();
                        Console.WriteLine($"Blob {dllNameInBlobStorage} in container {inboundContainerName} is {dllBlob.Properties.Length} bytes");
                        var memoryStream = new MemoryStream();
                        await dllBlob.DownloadToStreamAsync(memoryStream);
                        using (memoryStream)
                        {
                            var fileStream = File.Create(dllFileName);
                            memoryStream.Position = 0;
                            memoryStream.CopyTo(fileStream);
                            fileStream.Close();
                        }
                        Console.WriteLine($"{dllNameInBlobStorage} was downloaded to local file system {directoryToCreate}");
                        Console.WriteLine($"Directrory Listing:");
                        var files = Directory.GetFiles(directoryToCreate);
                        foreach (var fileName in files)
                        {
                            var fileInfo = new FileInfo($"{fileName}");
                            Console.WriteLine($"Name={fileInfo.Name}, length={fileInfo.Length}");
                        }
                        dllLoadPath = dllFileName;

                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Error processing local storage {e.Message}", e);
                    }
                }

                var currentColour = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"DLL Load path is {dllLoadPath}");
                Console.ForegroundColor = currentColour;
                // call a function in the DLL
                IntPtr hModule = LoadLibrary(dllLoadPath);
                if (hModule == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    throw new Exception($"Failed to load library {dllLoadPath} (ErrorCode: {errorCode})");
                }
                Console.WriteLine($"{DateTime.Now.ToString()} library {dllLoadPath} was loaded sucessfully. hModule={hModule}");

                IntPtr funcaddr = GetProcAddress(hModule, functionName);
                if (funcaddr == IntPtr.Zero)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    throw new Exception($"Failed to find function {functionName} (ErrorCode: {errorCode})");
                }
                Console.WriteLine($"{DateTime.Now.ToString()} function {functionName} found in library {dllLoadPath} address={funcaddr}");

                //IsHibernateAllowed isHibernateAllowed = Marshal.GetDelegateForFunctionPointer(funcaddr, typeof(IsHibernateAllowed)) as IsHibernateAllowed;
                //bool hibernateAllowed = isHibernateAllowed.Invoke();
                //Console.WriteLine($"{DateTime.Now.ToString()} function {functionName} executed sucessfully!");
                //if (hibernateAllowed) Console.WriteLine($"{DateTime.Now.ToString()} Hibernate Allowed!");
                //else Console.WriteLine($"{DateTime.Now.ToString()} Hibernate NOT Allowed!");

                NicksStringFunction stringFunction = Marshal.GetDelegateForFunctionPointer<NicksStringFunction>(funcaddr) as NicksStringFunction;
                IntPtr stringResultPtr = stringFunction();
                string stringResult = Marshal.PtrToStringBSTR(stringResultPtr);
                Console.WriteLine($"{DateTime.Now.ToString()} function {functionName} returned \"{stringResult}\"");


                if (hModule != IntPtr.Zero)
                {
                    FreeLibrary(hModule);
                    Console.WriteLine($"{DateTime.Now.ToString()} library {dllLoadPath} was unloaded");
                };
                Console.WriteLine($"{DateTime.Now.ToString()} DLLStuff completed normally");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error {e.Message}");

            }
            try
            {
                Console.WriteLine("Press Enter to continue");
                Console.ReadLine();
            }
            catch (Exception) { }


        }

        private static void WriteEnvironmentData()
        {
            string str;
            string nl = Environment.NewLine;
            //
            Console.WriteLine("*************************************************************************************************************");
            Console.WriteLine("-- Environment members --");

            //  Invoke this sample with an arbitrary set of command line arguments.
            Console.WriteLine("CommandLine: {0}", Environment.CommandLine);

            string[] arguments = Environment.GetCommandLineArgs();
            Console.WriteLine("GetCommandLineArgs: {0}", String.Join(", ", arguments));

            //  <-- Keep this information secure! -->
            Console.WriteLine("CurrentDirectory: {0}", Environment.CurrentDirectory);

            Console.WriteLine("ExitCode: {0}", Environment.ExitCode);

            Console.WriteLine("HasShutdownStarted: {0}", Environment.HasShutdownStarted);

            //  <-- Keep this information secure! -->
            Console.WriteLine("MachineName: {0}", Environment.MachineName);

            Console.WriteLine("NewLine: {0}  first line{0}  second line{0}  third line",
                                  Environment.NewLine);

            Console.WriteLine("OSVersion: {0}", Environment.OSVersion.ToString());

            Console.WriteLine("StackTrace: '{0}'", Environment.StackTrace);

            //  <-- Keep this information secure! -->
            Console.WriteLine("SystemDirectory: {0}", Environment.SystemDirectory);

            Console.WriteLine("TickCount: {0}", Environment.TickCount);

            //  <-- Keep this information secure! -->
            Console.WriteLine("UserDomainName: {0}", Environment.UserDomainName);

            Console.WriteLine("UserInteractive: {0}", Environment.UserInteractive);

            //  <-- Keep this information secure! -->
            Console.WriteLine("UserName: {0}", Environment.UserName);

            Console.WriteLine("Version: {0}", Environment.Version.ToString());

            Console.WriteLine("WorkingSet: {0}", Environment.WorkingSet);

            //  No example for Exit(exitCode) because doing so would terminate this example.

            //  <-- Keep this information secure! -->
            string query = "My system drive is %SystemDrive% and my system root is %SystemRoot%";
            str = Environment.ExpandEnvironmentVariables(query);
            Console.WriteLine("ExpandEnvironmentVariables: {0}  {1}", nl, str);

            Console.WriteLine("GetEnvironmentVariable: {0}  My temporary directory is {1}.", nl,
                                   Environment.GetEnvironmentVariable("TEMP"));

            Console.WriteLine("GetEnvironmentVariables: ");
            IDictionary environmentVariables = Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry de in environmentVariables)
            {
                Console.WriteLine("  {0} = {1}", de.Key, de.Value);
            }

            Console.WriteLine("GetFolderPath: {0}",
                         Environment.GetFolderPath(Environment.SpecialFolder.System));

            string[] drives = Environment.GetLogicalDrives();
            Console.WriteLine("GetLogicalDrives: {0}", String.Join(", ", drives));
            Console.WriteLine("*************************************************************************************************************");
        }
    }
}
