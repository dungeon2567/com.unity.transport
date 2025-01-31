# Frequently asked questions

## Which endpoint should I bind to?

### For clients

As a rule of thumb, clients should not call the `Bind` method. The default behavior is to automatically bind to an [ephemeral port](https://en.wikipedia.org/wiki/Ephemeral_port) when connecting, which is the desired behavior 99% of the time.

The only time you would want to bind to a particular endpoint is if a firewall requires a particular source port to be used. This is uncommon, and is typically only seen in very restrictive corporate environments.

### For servers

Servers should typically bind to IP address 0.0.0.0 (or its IPv6 equivalent) and a port of your choosing. This IP address is usually called the wildcard address. Here's an example of binding to port 7777:

```csharp
driver.Bind(NetworkEndpoint.AnyIpv4.WithPort(7777));
```

The only reason you would want to bind to a specific IP address instead of the wildcard is if your server has multiple interfaces and you want to limit clients to connect only through a particular one. This can be because of security reasons, or because another service is already listening on the same port on a different interface.

### When using Unity Relay

When using [Unity Relay](https://unity.com/products/relay), there is no real need to use a known port since all communications will occur through the Relay servers. In this situation, we recommend that even servers should bind to an ephemeral port. This can be achieved with the following call:

```csharp
driver.Bind(NetworkEndpoint.AnyIpv4);
```

## Why is `Bind` returning an error?

`NetworkDriver.Bind` will return an error (a negative value) if the socket has failed to be created, or if it has failed to be bound to its endpoint. There can be many reasons for that. Here are the most common ones:

1. The endpoint doesn't exist on the local machine. This is common when binding to a public IP address on a machine behind a LAN (for example, binding to the address obtained from [whatismyip.com](https://www.whatismyip.com/) when your machine is behind a router). Refer to the previous question regarding which endpoint to bind to.
2. The port is already used by another service. The `netstat` command line utility can be used to diagnose this. For example, on Windows use the command `netstat -a -p UDP` to list UDP endpoints already in use.
3. Binding to a port lower than 1024. On some platforms (e.g. Linux), this requires elevated privileges.
4. Your user doesn't have the permission to create sockets. Some platforms will disallow the creation of sockets for restricted users. For example, on Android the application might require [extra permissions](https://docs.unity3d.com/2022.2/Documentation/Manual/android-permissions-in-unity.html).

## Why isn't my client connecting?

Common causes for a client failing to connect:

1. Binding to an improper endpoint on the server (see above).
2. The server's firewall is misconfigured. Try disabling it. If it solves the issue, you can then add a proper exception to its configuration (don't leave it disabled!).
3. The client simply can't reach the server. Command line utilities like `ping` and `traceroute` (`tracert` on Windows) can be used to test the reachability of another machine on the network. It is also possible to observe the traffic being sent/received on a machine with tools like [Wireshark](https://www.wireshark.org/).

## How can I modify the connection/disconnection timeouts?

Connection and disconnection timeouts can be modified through custom `NetworkSettings` when creating the `NetworkDriver`:

```csharp
var settings = new NetworkSettings();
settings.WithNetworkConfigParameters(
    connectTimeoutMS: 500,
    maxConnectAttempts: 10,
    disconnectTimeoutMS: 10000);

var driver = NetworkDriver.Create(settings);
```

The above code will create a `NetworkDriver` where establishing a connection fails after 5 seconds (a maximum of 10 attempts, every 500 milliseconds), and where connections will be closed after 10 seconds of inactivity. The defaults are respectively 1 minute (60 attempts every second) and 30 seconds.

## Why was my connection closed?

The reason for a `Disconnect` event can be obtained from the event's `DataStreamReader`:

```csharp
var eventType = driver.PopEvent(out _, out var streamReader);
if (eventType == NetworkEvent.Type.Disconnect)
{
    var disconnectReason = streamReader.ReadByte();
}
```

The obtained value is from the `Error.DisconnectReason` enum and indicates why the `Disconnect` event was generated.

## What's the largest message I can send?

By default, the size of messages is limited by the [MTU](https://en.wikipedia.org/wiki/Maximum_transmission_unit), which ensures messages are not larger than a single IP packet on most network configurations. Because different protocols and pipelines will have different overhead, the size of the maximum useful payload that can be written to a `DataStreamWriter` may vary. There are two ways to obtain this value:

```csharp
// 1. Directly by substracting headers from the MTU.
var maxPayloadSize = NetworkParameterConstants.MTU - driver.MaxHeaderSize(pipeline);

// 2. By looking at the capacity of a DataStreamWriter.
driver.BeginSend(pipeline, connection, out var writer);
var maxPayloadSize = writer.Capacity;
driver.AbortSend(writer);
```

To send messages larger than that, use a pipeline with the `FragmentationPipelineStage`. Refer to the section on [using pipelines](pipelines-usage.md) for more information.

## What does error `NetworkSendQueueFull` mean?

Both `BeginSend` and `EndSend` can return error code `NetworkSendQueueFull` (value -5).

### If returned by `BeginSend`

It means a buffer for the new message could not be acquired from the send queue. This could indicate that the send queue capacity is insufficient for the workload. The capacity can be increased when creating the `NetworkDriver`:

```csharp
var settings = new NetworkSettings();
settings.WithNetworkConfigParameters(
    sendQueueCapacity: 256
    receiveQueueCapacity: 256);

var driver = NetworkDriver.Create(settings);
```

As the example above demonstrates, it is often a good idea to set the receive queue capacity to the same value. Failing to do so could add latency to the processing of packets, or even cause them to be dropped. This is because if the receive queue is full, newly-received packets will have to wait in OS buffers to be processed. If the OS buffers are full, new packets will be dropped.

The default value for both send and receive queue capacity is 64. Increasing the values will result in increased memory usage (the impact is about 1500 bytes per unit of capacity).

### If returnd by `EndSend`

This can only happen when sending on a pipeline with a `ReliableSequencedPipelineStage`. It indicates that there already 32 reliable packets in flight, which is the maximum. Refer to the section on [using pipelines](pipelines-usage.md) or to the question below for tips on how to deal with this situation.

## Can I increase the limit of 32 packets in flight for the reliable pipeline?

It is possible to increase it to 64. See the section on [using pipelines](pipelines-usage.md) for details. Unfortunately, it is currently impossible to increase it further than that.

However, if your application has different streams of data that require reliability and sequencing, but the ordering of messages between the streams doesn't matter, then it is possible to somewhat circumvent the limit by creating multiple reliable pipelines. That is because the limit is both per connection *and per pipeline*.

For example, you could create a pipeline for RPCs and another one for chat messages:

```csharp
var rpcPipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
var chatPipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
```

Each pipeline will have its own limit of 32/64 messages in flight. Note however that ordering between the two pipelines is *not* guaranteed. So sending a message on `rpcPipeline` and then sending a message on `chatPipeline` does not mean that the RPC will be delivered first.
