@echo off
cd /d "%~dp0"
py probe_disk.py 1 > probe_out.txt 2>&1
