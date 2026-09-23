' Starts Glassy System Gadget with no console window. Double-click it, or from the Run box:
'   wscript.exe "S:\AI_Dev\Projects\Incubating\Glassy_System-Gadget\Glassy.vbs"
' Launching it again while the widget is running opens Settings in the running widget.
Set fso = CreateObject("Scripting.FileSystemObject")
dll = fso.GetParentFolderName(WScript.ScriptFullName) & "\src\Glassy.App\bin\Release\net8.0-windows\Glassy.App.dll"
If Not fso.FileExists(dll) Then
    MsgBox "Not built yet. Run:" & vbCrLf & "dotnet build -c Release", 48, "Glassy System Gadget"
    WScript.Quit 1
End If
CreateObject("WScript.Shell").Run "dotnet """ & dll & """", 0, False
