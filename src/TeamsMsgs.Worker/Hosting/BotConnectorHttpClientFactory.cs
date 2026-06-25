// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

namespace TeamsMsgs.Worker.Hosting;

/// <summary>
/// Fornece HttpClients para o conector do Bot Framework com um timeout curto.
///
/// O default do HttpClient é 100s. Em envios em massa, um destinatário que faz a
/// requisição "pendurar" prende um slot de concorrência por 100s e ainda é
/// reentregue MaxDeliveryCount vezes — derrubando a vazão. Um timeout curto faz a
/// falha ser rápida (tratada como transitória) e libera o slot.
///
/// Compartilha um único <see cref="SocketsHttpHandler"/> (com pooling) entre os
/// clients para evitar exaustão de sockets sob alta concorrência.
/// </summary>
internal sealed class BotConnectorHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly SocketsHttpHandler _handler;

    public BotConnectorHttpClientFactory(TimeSpan timeout)
    {
        _timeout = timeout;
        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Pods em AKS injetam HTTPS_PROXY; o conector deve falar direto com o
            // serviceUrl do Teams, então desabilitamos o proxy para evitar desvios.
            UseProxy = false,
        };
    }

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { Timeout = _timeout };

    public void Dispose() => _handler.Dispose();
}
