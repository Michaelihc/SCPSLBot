using CommandSystem;
using System;
using System.Reflection;

namespace SCPSLBot.PlaytestScenarios.Harness;

/// <summary>
/// Central adapter over native SCP:SL command processors. The only reflection in this scenario
/// assembly targets native internal dispatch entry points; it never looks up SCPSLBot types or
/// members. Commands therefore traverse CommandExecuting/CommandExecuted and the registered real
/// command instance exactly as an administrator or server-console operator would.
/// </summary>
internal static class NativeCommandAdapter
{
    private static readonly MethodInfo ProcessRemoteAdminQuery =
        typeof(RemoteAdmin.CommandProcessor).GetMethod(
            "ProcessQuery",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(string), typeof(CommandSender) },
            modifiers: null)
        ?? throw new MissingMethodException(typeof(RemoteAdmin.CommandProcessor).FullName, "ProcessQuery");

    private static readonly MethodInfo ProcessGameConsoleQuery =
        typeof(GameCore.Console).GetMethod(
            "TypeCommand",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(string), typeof(CommandSender) },
            modifiers: null)
        ?? throw new MissingMethodException(typeof(GameCore.Console).FullName, "TypeCommand");

    public static NativeCommandResult RemoteAdmin(string query, bool fullPermissions = true)
    {
        CaptureSender sender = new(fullPermissions, "SCPSLBOT-PLAYTEST-RA");
        string response = Invoke(ProcessRemoteAdminQuery, null, query, sender);
        return new NativeCommandResult(response, sender.LastReply, sender.LastSuccess, sender.ReplyCount);
    }

    public static bool IsGameConsoleCommandRegistered(string command) =>
        GameCore.Console.ConsoleCommandHandler.TryGetCommand(command, out _);

    public static NativeCommandResult GameConsole(string query)
    {
        CaptureSender sender = new(fullPermissions: true, "SCPSLBOT-PLAYTEST-CONSOLE");
        string response = Invoke(ProcessGameConsoleQuery, GameCore.Console.Singleton, query, sender);
        return new NativeCommandResult(response, sender.LastReply, sender.LastSuccess, sender.ReplyCount);
    }

    private static string Invoke(MethodInfo method, object? instance, string query, CaptureSender sender)
    {
        try
        {
            return method.Invoke(instance, new object[] { query, sender }) as string ?? string.Empty;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }
    }

    private sealed class CaptureSender : CommandSender
    {
        private readonly bool fullPermissions;
        private readonly string senderId;

        public CaptureSender(bool fullPermissions, string senderId)
        {
            this.fullPermissions = fullPermissions;
            this.senderId = senderId;
        }

        public string LastReply { get; private set; } = string.Empty;
        public bool LastSuccess { get; private set; }
        public int ReplyCount { get; private set; }

        public override string SenderId => senderId;
        public override string Nickname => "SCPSLBot playtest verifier";
        public override ulong Permissions => fullPermissions ? ulong.MaxValue : 0UL;
        public override byte KickPower => fullPermissions ? byte.MaxValue : (byte)0;
        public override bool FullPermissions => fullPermissions;

        public override void RaReply(string text, bool success, bool logToConsole, string overrideDisplay)
        {
            LastReply = text ?? string.Empty;
            LastSuccess = success;
            ReplyCount++;
        }

        public override void Print(string text)
        {
            LastReply = text ?? string.Empty;
            ReplyCount++;
        }

        public override bool Available() => true;
    }
}

internal readonly struct NativeCommandResult
{
    public NativeCommandResult(string response, string lastReply, bool success, int replyCount)
    {
        Response = response;
        LastReply = lastReply;
        Success = success;
        ReplyCount = replyCount;
    }

    public string Response { get; }
    public string LastReply { get; }
    public bool Success { get; }
    public int ReplyCount { get; }

    public string CombinedText => string.IsNullOrWhiteSpace(LastReply)
        ? Response
        : $"{Response}\n{LastReply}";

    public override string ToString() =>
        $"success={Success} replies={ReplyCount} response='{Response}' lastReply='{LastReply}'";
}
