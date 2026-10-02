using Amazon;
using Amazon.EC2;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using MonitorDashboard.Models;
namespace MonitorDashboard.Services;
public sealed class AwsService(DashboardOptions options)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private object? cached;
    private DateTime expires;
    public async Task<object> InstancesAsync(CancellationToken ct)
    {
        if (!options.Aws.Enabled) return new { configured = false };
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && expires > DateTime.UtcNow) return cached;
            var result = await FetchAsync(ct);
            cached = result;
            expires = DateTime.UtcNow.AddMinutes(1);
            return result;
        }
        finally { gate.Release(); }
    }
    private async Task<object> FetchAsync(CancellationToken ct)
    {
        var region = RegionEndpoint.GetBySystemName(options.Aws.Region);
        // The SDK uses the standard credentials chain: IAM role, environment, or AWS profile.
        using var ec2 = new AmazonEC2Client(region);
        using var cw = new AmazonCloudWatchClient(region);
        var request = new Amazon.EC2.Model.DescribeInstancesRequest();
        if (options.Aws.InstanceIds.Length > 0) request.InstanceIds = options.Aws.InstanceIds.ToList();
        var instances = new List<object>();
        do
        {
            var response = await ec2.DescribeInstancesAsync(request, ct);
            foreach (var instance in (response.Reservations ?? []).SelectMany(r => r.Instances ?? []))
            {
                if (instances.Count >= 100) break;
                var metrics = new Dictionary<string, object?>();
                foreach (var metric in new[] { "CPUUtilization", "NetworkIn", "NetworkOut", "StatusCheckFailed" })
                {
                    var result = await cw.GetMetricStatisticsAsync(new GetMetricStatisticsRequest
                    {
                        Namespace = "AWS/EC2", MetricName = metric,
                        Dimensions = [new Dimension { Name = "InstanceId", Value = instance.InstanceId }],
                        StartTime = DateTime.UtcNow.AddMinutes(-Math.Clamp(options.Aws.MetricWindowMinutes, 5, 1440)),
                        EndTime = DateTime.UtcNow,
                        Period = Math.Max(60, options.Aws.MetricPeriodSeconds / 60 * 60),
                        Statistics = [metric.StartsWith("Network") ? "Sum" : metric == "StatusCheckFailed" ? "Maximum" : "Average"]
                    }, ct);
                    var point = (result.Datapoints ?? []).OrderByDescending(d => d.Timestamp).FirstOrDefault();
                    metrics[metric] = point is null ? null : new { at = point.Timestamp, value = metric.StartsWith("Network") ? point.Sum : metric == "StatusCheckFailed" ? point.Maximum : point.Average, unit = point.Unit?.Value };
                }
                instances.Add(new { id = instance.InstanceId, name = instance.Tags?.FirstOrDefault(t => t.Key == "Name")?.Value, state = instance.State?.Name?.Value, type = instance.InstanceType?.Value, address = instance.PrivateIpAddress, metrics });
            }
            request.NextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(request.NextToken) && instances.Count < 100);
        return new { configured = true, region = options.Aws.Region, instances, limitedTo = 100 };
    }
}
