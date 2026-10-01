@echo off
rem Runs Packwright in command-line mode. A batch file waits for the program, so the prompt returns after the output.
"%~dp0Packwright.exe" %*
exit /b %errorlevel%
