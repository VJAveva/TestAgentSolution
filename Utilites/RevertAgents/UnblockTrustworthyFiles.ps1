<# UNBLOCK THE TRUSTWORTHY FILES
********************************************************************************************************************************************
	REM Title: UNBLOCKTRUSTWORTHYFILES.PS1
	REM Purpose: Unblocks the files for usage which are copied from the external location. 
	REM Usgae:	Execute the Powershell script on the user specified folder.
	REM Author: Siva Thota - AppServer
********************************************************************************************************************************************
#>

cd\
cd $args[0]
Write-Host $Pwd
Get-ChildItem *.* -Recurse | Unblock-File