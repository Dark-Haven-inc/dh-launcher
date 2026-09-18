using System.Net;
using System.Net.Sockets;

namespace DarkHaven.Launcher.Update;

/// <summary>
/// Turns a failed connect into something a player can act on. The connect card used to print the
/// raw exception — "Response status code does not indicate success: 503 (Service Unavailable)." —
/// in English, on a Russian screen. Our own exceptions are already written for players and pass
/// through untouched.
/// </summary>
public static class LaunchErrors
{
    public static string Describe(Exception e) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "Сервер не отдаёт информацию для подключения (404). Проверьте адрес.",
        HttpRequestException { StatusCode: { } code } when (int)code >= 500 =>
            $"Сервер ответил ошибкой {(int)code} — скорее всего, он перезапускается. Попробуйте через минуту.",
        HttpRequestException { StatusCode: { } code } =>
            $"Сервер отклонил запрос (код {(int)code}).",
        HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.HostNotFound } } =>
            "Адрес сервера не найден. Проверьте, нет ли в нём опечатки.",
        HttpRequestException =>
            "Не удалось связаться с сервером — он выключен или недоступен из вашей сети.",
        TimeoutException or TaskCanceledException =>
            "Сервер не ответил вовремя. Попробуйте ещё раз.",
        _ => e.Message,
    };
}
