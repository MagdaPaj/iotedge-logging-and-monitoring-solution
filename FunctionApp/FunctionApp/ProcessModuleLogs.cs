using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net.Http;
using Newtonsoft.Json;
using FunctionApp.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using System.Threading.Tasks;

namespace FunctionApp
{
    public class ProcessModuleLogs
    {
        private string _hubResourceId = Environment.GetEnvironmentVariable("HubResourceId");
        private string _connectionString = Environment.GetEnvironmentVariable("StorageConnectionString");
        private string _containerName = Environment.GetEnvironmentVariable("ContainerName");
        private string _workspaceId = Environment.GetEnvironmentVariable("WorkspaceId");
        private string _workspaceKey = Environment.GetEnvironmentVariable("WorkspaceKey");
        private string _workspaceDomain = Environment.GetEnvironmentVariable("WorkspaceDomain");
        private string _workspaceApiVersion = Environment.GetEnvironmentVariable("WorkspaceApiVersion");
        private string _logsEncoding = Environment.GetEnvironmentVariable("LogsEncoding");
        private string _logType = Environment.GetEnvironmentVariable("LogType");
        private int _logMaxSizeMB = Convert.ToInt32(Environment.GetEnvironmentVariable("LogsMaxSizeMB"));
        private bool _compressForUpload = Convert.ToBoolean(Environment.GetEnvironmentVariable("CompressForUpload"));
        private AzureLogAnalytics _azureLogAnalytics;
        
        public ProcessModuleLogs(AzureLogAnalytics azureLogAnalytics)
        {
            this._azureLogAnalytics = azureLogAnalytics;
        }

        [FunctionName("ProcessModuleLogs")]
        public async Task Run(
            [QueueTrigger("%QueueName%", Connection = "StorageConnectionString")] string queueItem,
            ILogger log)
        {
            try
            {
                #region Queue trigger
                JObject storageEvent = JsonConvert.DeserializeObject<JObject>(queueItem);
                var match = Regex.Match(storageEvent["subject"].ToString(), "/blobServices/default/containers/(.*)/blobs/(.*)", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    log.LogWarning($"Unable to parse blob Url from {storageEvent["subject"]}");
                    return;
                }

                if (!string.Equals(match.Groups[1].Value, _containerName))
                {
                    log.LogDebug($"Ignoring queue item because it is related to container '{match.Groups[1].Value}'");
                    return;
                }

                string blobName = match.Groups[2].Value;
                #endregion

                log.LogInformation($"ProcessModuleLogs function received a new queue message from blob {blobName}");
                
                // Create a BlobServiceClient object which will be used to create a container client
                BlobServiceClient blobServiceClient = new BlobServiceClient(_connectionString);

                // Create container client object
                BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(_containerName);

                // Get blob client object
                BlobClient blobClient = containerClient.GetBlobClient(blobName);

                // Read the blob's contents
                Stream blobStream = await blobClient.OpenReadAsync();

                // Decompress if encoding is gzip
                string logsString = string.Empty;
                if (string.Equals(_logsEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
                    logsString = GZipCompression.Decompress(blobStream);
                else
                {
                    StreamReader reader = new StreamReader(blobStream);
                    logsString = reader.ReadToEnd();
                }

                IoTEdgeLog[] iotEdgeLogs = JsonConvert.DeserializeObject<IoTEdgeLog[]>(logsString);

                // Convert to logs their final log analytics format
                LogAnalyticsLog[] logAnalyticsLogs = iotEdgeLogs.Select(x => new LogAnalyticsLog(x)).ToArray();

                if (logAnalyticsLogs.Length == 0)
                    return;

                // because log analytics supports messages up to 30MB,
                // we have to break logs in chunks to fit in on each request
                byte[] logBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(logAnalyticsLogs));
                double chunks = Math.Ceiling(logBytes.Length / (_logMaxSizeMB * 1024f * 1024f));

                // get right number of items for the logs array
                int steps = Convert.ToInt32(Math.Ceiling(logAnalyticsLogs.Length / chunks));

                int count = 0;
                do
                {
                    int limit = count + steps < logAnalyticsLogs.Length ? count + steps : logAnalyticsLogs.Length;

                    log.LogInformation($"Submitting data collection request for logs {count + 1} - {limit} / {logAnalyticsLogs.Length}");

                    LogAnalyticsLog[] logsChunk = logAnalyticsLogs.Skip(count).Take(limit).ToArray();
                    try
                    {
                        //logAnalytics.Post(JsonConvert.SerializeObject(logsChunk), _logType, _hubResourceId);
                        bool success = this._azureLogAnalytics.PostToCustomTable(JsonConvert.SerializeObject(logsChunk), _logType, _hubResourceId);
                        if (success)
                            log.LogInformation("ProcessModuleLogs request to log analytics completed successfully");
                        else
                            log.LogError("ProcessModuleLogs request to log analytics failed");
                    }
                    catch (Exception e)
                    {
                        log.LogError($"ProcessModuleLogs failed with exception {e}");
                    }

                    count += steps;
                }
                while (count < iotEdgeLogs.Length);

                // Delete blob after being processed
                await blobClient.DeleteIfExistsAsync(Azure.Storage.Blobs.Models.DeleteSnapshotsOption.IncludeSnapshots);
            }
            catch (Exception e)
            {
                log.LogError($"ProcessModuleLogs failed with the following exception: {e}");
            }
        }
    }
}