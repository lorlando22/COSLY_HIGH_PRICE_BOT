' Launches run-bot.cmd with no console window.
'
' Task Scheduler tasks that run as the logged-on user show their console, and this one
' stays open for a whole day. The bot reports through Telegram and publish\Logs anyway,
' so the window would be pure noise. Run run-bot.cmd directly when you do want to watch it.
' The last argument must stay True: it makes wscript wait for the bot instead of exiting
' immediately. Task Scheduler judges the task finished the moment its action returns, so
' with False the task would drop back to Ready, its "do not start a new instance" rule
' would have nothing to suppress, and the watchdog repetition would launch a second bot
' every five minutes - each with its own memory, so every alert would go out twice.
Dim shell, folder
Set shell = CreateObject("WScript.Shell")
folder = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
shell.Run """" & folder & "run-bot.cmd""", 0, True
