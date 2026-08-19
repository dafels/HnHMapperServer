using System.Threading.Channels;

namespace HnHMapperServer.Services.Interfaces;

/// <summary>
/// A live subscription to one broadcast event stream. Disposing it is the ONLY way to
/// unregister the subscriber: until then the service keeps the underlying channel alive
/// and every future event is buffered into it, whether or not anyone is still reading.
/// SSE handlers must therefore hold each subscription in a using/try-finally scope.
/// </summary>
public interface IChannelSubscription<T> : IDisposable
{
    ChannelReader<T> Reader { get; }
}
