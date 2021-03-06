﻿namespace Microsoft.HpcAcm.Services.JobMonitor
{
    using Microsoft.HpcAcm.Common.Dto;
    using Microsoft.HpcAcm.Common.Utilities;
    using Microsoft.HpcAcm.Services.Common;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Microsoft.WindowsAzure.Storage.Table;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using T = System.Threading.Tasks;

    class JobFinisher : JobActionHandlerBase
    {
        public override async T.Task ProcessAsync(Job job, JobEventMessage message, CancellationToken token)
        {
            var jobTable = this.Utilities.GetJobsTable();

            this.Logger.Information("JobFinisher, job {0}, state {1}", job.Id, job.State);
            if (job.State != JobState.Finishing)
            {
                return;
            }

            var jobPartitionKey = this.Utilities.GetJobPartitionKey(job.Type, job.Id);

            var jobPartitionQuery = this.Utilities.GetPartitionQueryString(jobPartitionKey);
            var taskResultRangeQuery = this.Utilities.GetRowKeyRangeString(
                this.Utilities.GetTaskResultKey(job.Id, 0, job.RequeueCount),
                this.Utilities.GetTaskResultKey(job.Id, int.MaxValue, job.RequeueCount),
                false,
                false);

            this.Logger.Information("JobFinisher, job {0}, querying tasks results", job.Id);
            var allTaskResults = (await jobTable.QueryAsync<ComputeClusterTaskInformation>(
                TableQuery.CombineFilters(jobPartitionQuery, TableOperators.And, taskResultRangeQuery),
                null,
                token))
                .Select(t => t.Item3)
                .ToList();

            var taskRangeQuery = this.Utilities.GetRowKeyRangeString(
                this.Utilities.GetTaskKey(job.Id, 0, job.RequeueCount),
                this.Utilities.GetTaskKey(job.Id, int.MaxValue, job.RequeueCount),
                false,
                false);

            this.Logger.Information("JobFinisher, job {0}, querying tasks", job.Id);
            var allTasks = (await jobTable.QueryAsync<Task>(
                TableQuery.CombineFilters(jobPartitionQuery, TableOperators.And, taskRangeQuery),
                null,
                token))
                .Select(t => t.Item3)
                .Where(t => t.CustomizedData != Task.EndTaskMark)
                .ToList();

            this.Logger.Information("JobFinisher, job {0}, aggregating results", job.Id);
            var aggregationResult = await this.JobTypeHandler.AggregateTasksAsync(job, allTasks, allTaskResults, token);

            this.Logger.Information("JobFinisher, job {0}, aggregated result", job.Id);
            if (job.State == JobState.Finishing)
            {
                var finalState = job.State == JobState.Finishing ? JobState.Finished : JobState.Canceled;
                if (job.FailJobOnTaskFailure && allTasks.Any(t => t.State == TaskState.Failed))
                {
                    finalState = JobState.Failed;
                    (job.Events ?? (job.Events = new List<Event>())).Add(new Event()
                    {
                        Content = $"Fail the job because some tasks failed.",
                        Source = EventSource.Job,
                        Type = EventType.Alert
                    });
                }

                job.State = finalState;
            }

            this.Logger.Information("JobFinisher, job {0}, saving result, length {1}", job.Id, aggregationResult?.Length);

            if (aggregationResult != null)
            {
                var jobOutputBlob = await this.Utilities.CreateOrReplaceJobOutputBlobAsync(job.Type, this.Utilities.GetJobAggregationResultKey(job.Id), token);
                await jobOutputBlob.AppendTextAsync(aggregationResult, Encoding.UTF8, null, null, null, token);
            }

            await this.Utilities.UpdateJobAsync(job.Type, job.Id, j =>
            {
                j.State = job.State;
                // TODO: separate the events.
                j.Events = job.Events;
            }, token, this.Logger);

            this.Logger.Information("JobFinisher, job {0}, canceling tasks on nodes", job.Id);

            await T.Task.WhenAll(job.TargetNodes.Select(async n =>
            {
                var q = this.Utilities.GetNodeCancelQueue(n);
                var msg = new CloudQueueMessage(
                    JsonConvert.SerializeObject(new TaskEventMessage() { JobId = job.Id, Id = 0, JobType = job.Type, RequeueCount = job.RequeueCount, EventVerb = "cancel" }));
                await q.AddMessageAsync(msg, null, null, null, null, token);
                this.Logger.Information("Added job {0} cancel to queue {1}, {2}", job.Id, q.Name, msg.Id);
            }));

            this.Logger.Information("JobFinisher, job {0}, canceling tasks on nodes", job.Id);
        }
    }
}
