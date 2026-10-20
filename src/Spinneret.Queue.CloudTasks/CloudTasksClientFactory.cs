using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.CloudTasks;

internal static class CloudTasksClientFactory
{
    public static CloudTasksClient Create(IOptions<CloudTasksOptions> options)
    {
        var value = options.Value;

        if (value.UsesEmulator)
        {
            return new CloudTasksClientBuilder
            {
                Endpoint = value.EmulatorEndpoint,
                ChannelCredentials = ChannelCredentials.Insecure,
            }.Build();
        }

        return CloudTasksClient.Create();
    }
}
