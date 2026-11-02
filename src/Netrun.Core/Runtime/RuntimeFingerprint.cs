using System.Security.Cryptography;
using System.Text;
using Netrun.Core.Routing;
using Netrun.Core.Transport;
using Netrun.Core.Discovery;
using Netrun.Core.Projection;

namespace Netrun.Core.Runtime;

/// <summary>Canonical identity всего logical frame, не process-local hash и не fingerprint пустого sync tick.</summary>
internal static class RuntimeFingerprint
{
    /// <summary>Проверяет ограниченную форму и кодирует все поля команды; null означает исправимый invalid input.</summary>
    internal static byte[]? Compute(RuntimeInput input, int maxEntries)
    {
        if (input.Sequence == 0 || input.SimulationTimeDs < 0 || input.Command is null)
            return null;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write("NETRUN-RUNTIME-1");
            writer.Write(input.Epochs.World);
            writer.Write(input.Epochs.Engine);
            writer.Write(input.Epochs.Binding);
            writer.Write(input.Sequence);
            writer.Write(input.SimulationTimeDs);
            switch (input.Command)
            {
                case ApplyWorld world:
                    var facts = SynchronizationFrameRules.Fingerprint(new InputBatch(input.Epochs, input.Sequence,
                        input.SimulationTimeDs, world.Entries), maxEntries);
                    if (facts is null)
                        return null;
                    writer.Write((byte)1);
                    writer.Write(facts);
                    break;
                case SubmitRequest submit when TransportFingerprint.ValidRequest(submit.Request):
                    writer.Write((byte)2);
                    writer.Write(TransportFingerprint.Request(submit.Request));
                    break;
                case RejectRequest reject when TransportFingerprint.ValidKey(reject.Key) && TransportFingerprint.ValidToken(reject.Code, 64):
                    writer.Write((byte)12);
                    writer.Write(reject.Key.ProducerId);
                    writer.Write(reject.Key.Sequence);
                    writer.Write(reject.Code);
                    break;
                case CancelRequest cancel when TransportFingerprint.ValidKey(cancel.Key):
                    writer.Write((byte)3);
                    writer.Write(cancel.Key.ProducerId);
                    writer.Write(cancel.Key.Sequence);
                    break;
                case FinishInvocation { Result: { } result } when NodeFactRules.IsValidText(result.BindingEpoch, 128) &&
                    NodeFactRules.IsValidText(result.InvocationId, 256) && (result.Payload is null || result.Payload.Validate()) &&
                    (result.Code is null || TransportFingerprint.ValidToken(result.Code, 64)):
                    writer.Write((byte)4);
                    writer.Write(TransportFingerprint.Result(result));
                    break;
                case QueryRoute route when ValidNode(route.Source) && ValidNode(route.Destination) && Enum.IsDefined(route.Mode):
                    writer.Write((byte)5);
                    WriteNode(writer, route.Source);
                    WriteNode(writer, route.Destination);
                    writer.Write((byte)route.Mode);
                    break;
                case ConfigureBackbone backbone:
                    writer.Write((byte)6);
                    writer.Write(backbone.Enabled);
                    break;
                case DiscoverServices discover when ServiceDiscovery.IsValidRequest(discover.Request):
                    var request = discover.Request;
                    writer.Write((byte)7);
                    writer.Write(request.Key.ProducerId);
                    writer.Write(request.Key.Sequence);
                    writer.Write(request.Source.ServiceId);
                    writer.Write(request.Source.Generation);
                    writer.Write(request.DestinationFamily);
                    writer.Write(request.RecipientTag is not null);
                    if (request.RecipientTag is { } tag) writer.Write(tag);
                    writer.Write(request.Communications);
                    writer.Write(request.Frequency.HasValue);
                    if (request.Frequency is { } frequency) writer.Write(frequency);
                    writer.Write(request.MaxResults);
                    break;
                case CancelDiscovery cancel when TransportFingerprint.ValidKey(cancel.Key):
                    writer.Write((byte)8);
                    writer.Write(cancel.Key.ProducerId);
                    writer.Write(cancel.Key.Sequence);
                    break;
                case QueryProjection projection when ProjectionBroker.IsValidRequest(projection.Request):
                    writer.Write((byte)9);
                    writer.Write(projection.Request.Key.ProducerId);
                    writer.Write(projection.Request.Key.Sequence);
                    writer.Write((int)projection.Request.Kind);
                    writer.Write((int)projection.Request.Mode);
                    writer.Write(projection.Request.Source is not null);
                    if (projection.Request.Source is { } source) WriteNode(writer, source);
                    break;
                case CancelProjection cancel when TransportFingerprint.ValidKey(cancel.Key):
                    writer.Write((byte)10);
                    writer.Write(cancel.Key.ProducerId);
                    writer.Write(cancel.Key.Sequence);
                    break;
                case QueryAnchor anchor when AnchorRequest.IsValid(anchor.Request):
                    writer.Write((byte)11);
                    writer.Write(anchor.Request.Key.ProducerId);
                    writer.Write(anchor.Request.Key.Sequence);
                    writer.Write(anchor.Request.Position.X);
                    writer.Write(anchor.Request.Position.Y);
                    writer.Write(anchor.Request.Position.Z);
                    writer.Write(anchor.Request.SectorId);
                    writer.Write(anchor.Request.BaseAreaId is not null);
                    if (anchor.Request.BaseAreaId is { } area) writer.Write(area);
                    break;
                case StartGraphSynchronization start when TransportFingerprint.ValidKey(start.Key):
                    writer.Write((byte)13);
                    writer.Write(start.Key.ProducerId);
                    writer.Write(start.Key.Sequence);
                    break;
                case ContinueGraphSynchronization next when TransportFingerprint.ValidKey(next.Key) && next.PageIndex > 0:
                    writer.Write((byte)14);
                    writer.Write(next.Key.ProducerId);
                    writer.Write(next.Key.Sequence);
                    writer.Write(next.PageIndex);
                    break;
                case CancelGraphSynchronization cancel when TransportFingerprint.ValidKey(cancel.Key):
                    writer.Write((byte)15);
                    writer.Write(cancel.Key.ProducerId);
                    writer.Write(cancel.Key.Sequence);
                    break;
                default:
                    return null;
            }
        }
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    /// <summary>Проверяет физическое поколение до кодирования opaque NodeId.</summary>
    private static bool ValidNode(NodeKey? key) => key is not null && key.Generation > 0 && NodeFactRules.IsValidText(key.NodeId, 128);

    /// <summary>Пишет обе части identity, исключая неоднозначность адресного объединения строк.</summary>
    private static void WriteNode(BinaryWriter writer, NodeKey key)
    {
        writer.Write(key.NodeId);
        writer.Write(key.Generation);
    }
}
