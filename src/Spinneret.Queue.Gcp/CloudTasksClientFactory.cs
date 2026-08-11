using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Gcp;

internal static class CloudTasksClientFactory
{
    public static CloudTasksClient Create(IOptions<GcpQueueOptions> options)
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
