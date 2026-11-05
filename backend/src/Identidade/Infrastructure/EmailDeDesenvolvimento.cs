using System.Collections.Concurrent;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Development implementation of <see cref="IEnviadorDeEmail"/> (spec 5.2.9): keeps the last messages in memory for tests and for a developer to read, and writes ONE technical-log line
/// per message with the subject only (never the recipient or the body: a body may carry a secret link in the recovery flow of ticket 09). Nothing leaves the process. The real
/// provider is open question 4 of the spec: until it is bound, no e-mail is really delivered.
/// </summary>
public sealed class EnviadorDeEmailDeDesenvolvimento(ITechnicalLog log) : IEnviadorDeEmail
{
    private const int Capacidade = 200;

    private readonly ConcurrentQueue<MensagemDeEmail> _enviadas = new();

    public IReadOnlyList<MensagemDeEmail> Enviadas => [.. _enviadas];

    public Task EnviarAsync(MensagemDeEmail mensagem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mensagem);
        _enviadas.Enqueue(mensagem);
        while (_enviadas.Count > Capacidade && _enviadas.TryDequeue(out _))
        {
        }

        log.Write(LogSeverity.Information, "email.desenvolvimento", new Dictionary<string, object?> { ["assunto"] = mensagem.Assunto });
        return Task.CompletedTask;
    }
}
