@echo off
rem Launch the image resize/crop/combine GUI (no console window)
set PYW=%LOCALAPPDATA%\Programs\Python\Python312\pythonw.exe
if not exist "%PYW%" set PYW=pythonw.exe
start "" "%PYW%" "%~dp0image_editor.py"
