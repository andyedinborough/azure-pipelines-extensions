using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;

namespace VstsServerTaskHelper
{
    public class JobStatusReportingHelper : IJobStatusReportingHelper
    {
        private readonly VstsMessage vstsContext;
        private readonly ILogger logger;
        private readonly ITaskClient taskClient;
        private readonly IDictionary<string, string> eventProperties;

        /// <summary>
        /// Optional timeline record name. If specified only this timeline will be updated instead of all timelines.
        /// </summary>
        private readonly string timelineRecordName;

        public Func<Uri, string, IList<ILogger>, bool, ITaskClient> CreateTaskHttpClient { get; set; }

        public Func<Uri, string, IBuildClient> CreateBuildClient { get; set; }

        public Func<Uri, string, IReleaseClient> CreateReleaseClient { get; set; }

        public JobStatusReportingHelper(VstsMessage vstsContext, ILogger logger, ITaskClient taskClient, string timelineRecordName = null)
        {
            this.vstsContext = vstsContext;
            this.logger = logger;
            this.taskClient = taskClient;
            this.timelineRecordName = timelineRecordName;
            this.eventProperties = this.vstsContext.GetMessageProperties();

            this.CreateTaskHttpClient = TaskClientFactory.GetTaskClient;
            this.CreateBuildClient = (uri, authToken) => new BuildClient(uri, new VssBasicCredential(string.Empty, authToken));
            this.CreateReleaseClient = (uri, authToken) => new ReleaseClient(uri, new VssBasicCredential(string.Empty, authToken));
        }

        public async Task ReportJobStarted(DateTimeOffset offsetTime, string message, CancellationToken cancellationToken)
        {
            var vstsPlanUrl = this.vstsContext.VstsPlanUri;
            var vstsUrl = this.vstsContext.VstsUri;
            var authToken = this.vstsContext.AuthToken;
            var planId = this.vstsContext.PlanId;
            var projectId = this.vstsContext.ProjectId;
            var jobId = this.vstsContext.JobId;
            var hubName = this.vstsContext.VstsHub.ToString();
            var eventTime = offsetTime.UtcDateTime;

            var buildHttpClientWrapper = this.CreateBuildClient(vstsUrl, authToken);
            var releaseHttpClientWrapper = this.CreateReleaseClient(vstsPlanUrl, authToken);
            var isSessionValid = await IsSessionValid(this.vstsContext, buildHttpClientWrapper, releaseHttpClientWrapper, cancellationToken).ConfigureAwait(false);
            if (!isSessionValid)
            {
                await this.logger.LogInfo("SessionAlreadyCancelled", "Skipping ReportJobStarted for cancelled or deleted build/release", this.eventProperties, cancellationToken).ConfigureAwait(false);
                return;
            }

            var startedEvent = new JobStartedEvent(jobId);
            await taskClient.RaisePlanEventAsync(projectId, hubName, planId, startedEvent, cancellationToken).ConfigureAwait(false);
            await logger.LogInfo("JobStarted", message, this.eventProperties, cancellationToken, eventTime);
        }

        public async Task ReportJobProgress(DateTimeOffset offsetTime, string message, CancellationToken cancellationToken)
        {
            var planId = this.vstsContext.PlanId;
            var projectId = this.vstsContext.ProjectId;
            var hubName = this.vstsContext.VstsHub.ToString();
            var timelineId = this.vstsContext.TimelineId;
            var eventTime = offsetTime.UtcDateTime;

            try
            {
                await logger.LogInfo("JobRunning", message, this.eventProperties, cancellationToken, eventTime);
            }
            catch (TaskOrchestrationPlanNotFoundException)
            {
                // ignore deleted builds
            }

            // Find all existing timeline records and set them to in progress state
            var records = await taskClient.GetRecordsAsync(projectId, hubName, planId, timelineId, userState: null, cancellationToken: cancellationToken).ConfigureAwait(false);

            var recordsToUpdate = GetTimelineRecordsToUpdate(records);
            foreach (var record in recordsToUpdate)
            {
                record.State = TimelineRecordState.InProgress;
            }

            await taskClient.UpdateTimelineRecordsAsync(projectId, hubName, planId, timelineId, recordsToUpdate, cancellationToken).ConfigureAwait(false);
        }
        
        public async Task ReportJobCompleted(DateTimeOffset offsetTime, string message, bool isPassed, CancellationToken cancellationToken)
        {
            var vstsPlanUrl = this.vstsContext.VstsPlanUri;
            var vstsUrl = this.vstsContext.VstsUri;
            var authToken = this.vstsContext.AuthToken;
            var planId = this.vstsContext.PlanId;
            var projectId = this.vstsContext.ProjectId;
            var jobId = this.vstsContext.JobId;
            var hubName = this.vstsContext.VstsHub.ToString();
            var timelineId = this.vstsContext.TimelineId;
            var eventTime = offsetTime.UtcDateTime;

            var buildHttpClientWrapper = this.CreateBuildClient(vstsUrl, authToken);
            var releaseHttpClientWrapper = this.CreateReleaseClient(vstsPlanUrl, authToken);
            var isSessionValid = await IsSessionValid(this.vstsContext, buildHttpClientWrapper, releaseHttpClientWrapper, cancellationToken).ConfigureAwait(false);
            if (!isSessionValid)
            {
                await this.logger.LogInfo("SessionAlreadyCancelled", "Skipping ReportJobStarted for cancelled or deleted build", this.eventProperties, cancellationToken).ConfigureAwait(false);
                return;
            }

            var completedEvent = new JobCompletedEvent(jobId, isPassed ? TaskResult.Succeeded : TaskResult.Failed);
            await taskClient.RaisePlanEventAsync(projectId, hubName, planId, completedEvent, cancellationToken).ConfigureAwait(false);

            if (isPassed)
            {
                await logger.LogInfo("JobCompleted", message, this.eventProperties, cancellationToken, eventTime);
            }
            else
            {
                await logger.LogError("JobFailed", message, this.eventProperties, cancellationToken, eventTime);
            }

            // Find all existing timeline records and close them
            await this.CompleteTimelineRecords(projectId, planId, hubName, timelineId, isPassed ? TaskResult.Succeeded : TaskResult.Failed, cancellationToken, taskClient);
        }

        internal async Task CompleteTimelineRecords(Guid projectId, Guid planId, string hubName, Guid parentTimelineId, TaskResult result, CancellationToken cancellationToken, ITaskClient taskClient)
        {
            // Find all existing timeline records and close them
            var records = await taskClient.GetRecordsAsync(projectId, hubName, planId, parentTimelineId, userState: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            var recordsToUpdate = GetTimelineRecordsToUpdate(records);

            foreach (var record in recordsToUpdate)
            {
                record.State = TimelineRecordState.Completed;
                record.PercentComplete = 100;
                record.Result = result;
                record.FinishTime = DateTime.UtcNow;
            }

            await taskClient.UpdateTimelineRecordsAsync(projectId, hubName, planId, parentTimelineId, recordsToUpdate, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<bool> IsSessionValid(VstsMessage vstsMessage, IBuildClient buildClient, IReleaseClient releaseClient, CancellationToken cancellationToken)
        {
            var projectId = vstsMessage.ProjectId;

            if (vstsMessage.VstsHub == HubType.Build)
            {
                var buildId = vstsMessage.BuildProperties.BuildId;
                return await BuildClient.IsBuildValid(buildClient, projectId, buildId, cancellationToken).ConfigureAwait(false);
            }

            if (vstsMessage.VstsHub == HubType.Release)
            {
                var releaseId = vstsMessage.ReleaseProperties.ReleaseId;
                return await ReleaseClient.IsReleaseValid(releaseClient, projectId, releaseId, cancellationToken).ConfigureAwait(false);
            }

            throw new NotSupportedException(string.Format("VstsHub {0} is not supported", vstsMessage.VstsHub));
        }

        private List<TimelineRecord> GetTimelineRecordsToUpdate(List<TimelineRecord> records)
        {
            if (string.IsNullOrEmpty(timelineRecordName))
            {
                return records.Where(rec => rec.Id == this.vstsContext.JobId || rec.ParentId == this.vstsContext.JobId)
                    .ToList();
            }

            return records.Where(rec => rec.Name != null && rec.Name.Equals(timelineRecordName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}