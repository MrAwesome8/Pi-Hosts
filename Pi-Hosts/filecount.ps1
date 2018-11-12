(Get-ChildItem $(get-location).Path -Recurse | where { !$_.PSisContainer }).count-1 | ft -a
pause