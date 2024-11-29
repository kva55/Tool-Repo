Set-ExecutionPolicy Unrestricted -Scope Process

Write-Output "ppscanner - PowerShell Port Scanner"
Write-Output "Author: Elysee Franchuk (kva55)"
Write-Output "github: https://github.com/kva55/ppscanner"
Write-Output ""

# Enter ip in arg ppscanner.ps1 192.168.1.1

# get ports
for ($i = 0; $i -lt $args.Length; $i++)
{
    if ($args[$i] -eq "--port" -or $args[$i] -eq "-p")
    {
        $portIndex = $args.IndexOf("--port")
        if ($portIndex -eq -1) 
        {
            $portIndex = $args.IndexOf("-p")
            if ($portIndex -ne -1 -and $portIndex + 1 -lt $args.Length)
            {
                $portList = $args[$portIndex + 1] 
                $portList = $portList -replace " ", "" # remove all spaces 
                $portList = $portList -split "," #split list with comma delimiter
                Write-Output "Selected ports: "
                Write-Output $portList
                Write-Output "" 
                
            } 
        }  
    }
}

# if no ports, continue with common port selection
if($portList -eq $null)
{
    Write-Output "No port values. Proceeding with common ports."
            # Set of common ports
            $portList = @(
	        7,20,21,22,23,
	        25,53,69,80,88,
	        102,110,135,137, 
            138,139,143,381,465,
	        383,443,445,464,2869,  
            587,593,636,691,5357,
            5040,902,912,989,995,
            1025,3389,1194,1337,
            1433,2179,4022,1434,
            1589,1725,1900,5353,
            5780,3702,2083,2483,
            2484,2967,3074,3306,
            3724,4664,5432,5900,
            6665,6666,6667,6668,
            6669,6881,6999,6970,
            8080,8081,8082,8087,
            8222,9100,10000,12345,
            27374,31337,2052,2053,
            2082,2083,2086,8443,8880
            )
            Write-Output ""
}

#get ips
for ($i = 0; $i -lt $args.Length; $i++)
{
    if ($args[$i] -eq "--target" -or $args[$i] -eq "-t")
    {
        $ipIndex = $args.IndexOf("--target")
        if ($ipIndex -eq -1) 
        {
            $ipIndex = $args.IndexOf("-t")
            if ($ipIndex -ne -1 -and $ipIndex + 1 -lt $args.Length)
            {
                $targetList = $args[$ipIndex + 1] 
                $targetList = $targetList -replace " ", "" # remove all spaces 
                $targetList = $targetList -split "," #split list with comma delimiter
                Write-Output "Proceeding to scan:"
                Write-Output $targetList
                #Write-Output "On ports: "
                #Write-Output $portList 
                
            } 
            else 
            {
                Write-Output "No targets specified with arg. Proceeding with:"
                $targetList = @(
                #"192.168.1.1",
                #"192.168.2.2",
                #"192.168.3.3",
                )
                Write-Output $targetList
                #Write-Output "On ports: "
                #Write-Output $portList  
            }
        }
    }
}

# suppress closed ports and show only open ports
$suppressClosedPorts = "true"

# bools for scan types
$smpt  = "false"
$http  = "false"
$wsman = "false"

# global timeout, if longer than 45 seconds to respond, likely filtered
$global_timeoutSec = New-TimeSpan -Seconds 45
$global_timeoutSec_int = 45

# global slow response - if responds after 15 seconds likely filtered
$global_slowtimeout = New-TimeSpan -Seconds 15
$slow_infer = "false" #turning this off might reduce FPs

# global fast response, if responds within 1 second likely filtered or up
$global_fastTimeout = New-TimeSpan -Second 1 # If unable to connect faster than other requests - filtered
$fast_infer = "false"

Write-Output ""
#get additional args
for ($i = 0; $i -lt $args.Length; $i++)
{
    # Show cloud ports
    if ($args[$i] -eq "--closed" -or $args[$i] -eq "-c")
    {
        $suppressClosedPorts = "false"
    }

    # Scan common cloudflare ports
    if ($args[$i] -eq "--cloudflare-check" -or $args[$i] -eq "-cfc")
    {
        # Common cloudflare ports
        $portList= @(
	    2052,2082,2086,8880,8080,8443
        )
        Write-Output "[*] Proceeding with cloudflare ports"
    }

    # Scan all ports
    if ($args[$i] -eq "--all-ports" -or $args[$i] -eq "-p-")
    {
        # All ports
        $portList = 1..65535
        Write-Output "[*] Proceeding with scanning all ports"
    }

    if ($args[$i] -eq "--smtp-scan" -or $args[$i] -eq "-smtp")
    {
        # All ports
        $smtp = "true"
        Write-Output "[*] Only scanning with smtp"
    }

    if ($args[$i] -eq "--http-scan" -or $args[$i] -eq "-http")
    {
        # All ports
        $http = "true"
        Write-Output "[*] Only scanning with http"
    }

    if ($args[$i] -eq "--wsman-scan" -or $args[$i] -eq "-wsman")
    {
        # All ports
        $wsman = "true"
        Write-Output "[*] Only scanning with wsman"
    }

    if ($args[$i] -eq "--web-ports" -or $args[$i] -eq "-w")
    {
        # common web ports
        $portList= @(
	    80,443,21,22,25,110,143,3389,8080,8443,3306,5432,1521,27017,6379,8081,8082,8083,8084,8085
        )
        Write-Output "[*] Proceeding with web application ports"
    }

    # Set timeout
    if ($args[$i] -eq "--timeout" -or $args[$i] -eq "-to")
    {
        
        
        Write-Output "[*] Proceeding with $timeoutIndex second timeout"
    }

    if ($args[$i] -eq "--fast-timeout" -or $args[$i] -eq "-fto") 
    {
        $fasttimeoutIndex = $args.IndexOf("--fast-timeout")
        if ($fasttimeoutIndex -eq -1) 
        {
            $fasttimeoutIndex = $args.IndexOf("-fto")
        }
    
        if ($fasttimeoutIndex -ne -1 -and $fasttimeoutIndex + 1 -lt $args.Length) 
        {
            $timeoutValue = $args[$fasttimeoutIndex + 1]

            if ($timeoutValue -is [int]) 
            {
                $global_fastTimeout = New-TimeSpan -Second $timeoutValue
                $fast_infer = "true"
            } 
            else 
            {
                $timeoutValue = 1
                $global_fastTimeout = New-TimeSpan -Second $timeoutValue
                $fast_infer = "true"
            }

        } 
        else 
        {
            $global_fastTimeout = New-TimeSpan -Second 1
            $fast_infer = "true"
        }
    
       Write-Output "[*] Proceeding with fast timeout inference at $($global_fastTimeout.TotalSeconds) second(s)"
    }


    if ($args[$i] -eq "--slow-timeout" -or $args[$i] -eq "-sto") 
    {
        $slowtimeoutIndex = $args.IndexOf("--slow-timeout")
        if ($slowtimeoutIndex -eq -1) 
        {
            $slowtimeoutIndex = $args.IndexOf("-sto")
        }
    
        if ($slowtimeoutIndex -ne -1 -and $slowtimeoutIndex + 1 -lt $args.Length) 
        {
            $timeoutValue = $args[$slowtimeoutIndex + 1]

            if ($timeoutValue -is [int]) 
            {
                $global_slowTimeout = New-TimeSpan -Second $timeoutValue
                $slow_infer = "true"
            } 
            else 
            {
                $timeoutValue = 15
                $global_slowTimeout = New-TimeSpan -Second $timeoutValue
                $slow_infer = "true"
            }

        } 
        else 
        {
            $global_slowTimeout = New-TimeSpan -Second 15
            $slow_infer = "true"
        }
    
       Write-Output "[*] Proceeding with slow timeout inference at $($global_slowTimeout.TotalSeconds) second(s)"
    }

    # Set timeout
    if ($args[$i] -eq "--timeout" -or $args[$i] -eq "-to")
    {
        $timeoutIndex = $args.IndexOf("--timeout")
        if ($timeoutIndex -eq -1) 
        {
            $timeoutIndex = $args.IndexOf("-to")
            if ($timeoutIndex -ne -1 -and $timeoutIndex + 1 -lt $args.Length)
            {
                $timeoutIndex = $args[$timeoutIndex + 1]
                $global_timeoutSec = New-TimeSpan -Seconds $timeoutIndex
                $global_timeoutSec_int = $timeoutIndex
            }
        }
        
        Write-Output "[*] Proceeding with $timeoutIndex second timeout"
    }

}

$sample= @(
	139, 445, 5357, 902, 912, 443,22
)


# Store ips and ports
$ipPortDict = @{}


Function smtp_portscan
{
	Param
    (
         [Parameter(Mandatory=$true, Position=0)]
         [string] $target,
         [Parameter(Mandatory=$true, Position=1)]
         [int] $port
    )
    $t = $target + ":" + $port
    
    try
    {
        $start = Get-Date # start recording request time
	    $resp = Send-MailMessage -To "recipient@example.com" -From "sender@example.com" -Subject "subject" -Body "body" -SmtpServer $target -Port $port -Verbose -ErrorAction Stop   
    }
    catch
    {
        $resp = $_.Exception.Message
    }

    # enable for debugging
    #Write-Output $resp
    
    $end1 = Get-Date
    $elapsed1 = $end1 - $start # get elapsed time
    #Write-Output $elapsed1
    if($resp -like "Unable to connect to the remote server" -and $elapsed1 -gt $global_timeoutSec)
    {
        Write-Output "[MAIL] $t - Open|filtered"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "Unable to connect to the remote server" -and $elapsed1 -lt $global_timeoutSec -and $suppressClosedPorts -eq "false")
    {
        Write-Output "[MAIL] $t - closed"
    }
    if($resp -like "The operation has timed out.")
    {
        Write-Output "[MAIL] $t - Open|filtered"
        $ipPortDict[$target] += @($port)
    }
    #if($resp -like "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host." -and $suppressClosedPorts -eq "false")
    #{
    #    Write-Output "[MAIL] $t - closed"
    #    #$ipPortDict[$target] += @($port)
    #}
    if($resp -like "Unable to read data from the transport connection: net_io_connectionclosed.")
    {
        Write-Output "[MAIL] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.")
    {
        Write-Output "[MAIL] $t - open"
        $ipPortDict[$target] += @($port)
    }
}

Function http_portscan
{
    
	Param
    (
         [Parameter(Mandatory=$true, Position=0)]
         [string] $target,
         [Parameter(Mandatory=$true, Position=1)]
         [int] $port
    )
    $t = $target + ":" + $port
    
    try
    {
        $start = Get-Date
	    $resp = Invoke-WebRequest -Uri $t -TimeoutSec $global_timeoutSec_int
        #Write-Output $resp
    }
    catch
    {
        $resp = $_.Exception.Message
        #Write-Output $resp
        
    }

    # enable for debugging
    #Write-Output $resp
    
    $end1 = Get-Date
    $elapsed1 = $end1 - $start
    #$elapsed1_format = $elapsed1 -Format "HH:mm:ss.fff"

    if($fast_infer -eq "true")
    {
        # Maybe higher FP - responses shorter than avg
        if($resp -like "Unable to connect to the remote server" -and $elapsed1 -lt $global_fastTimeout)
        {
            #Write-Output $elapsed1
            Write-Output "[HTTP] $t - open|filtered"
            #Write-Output "aaa"
            $ipPortDict[$target] += @($port)
        }
        if($resp -like "The underlying connection was closed: An unexpected error occurred on a receive." -and $elapsed1 -lt $global_fastTimeout)
        {
            Write-Output "[HTTP] $t - open"
            $ipPortDict[$target] += @($port)
        }
        elseif($resp -like "The underlying connection was closed: An unexpected error occurred on a receive." -and $elapsed1 -gt $global_fastTimeout)
        {
            Write-Output "[HTTP] $t - closed"
            #$ipPortDict[$target] += @($port)
        }
        
    }

    if($slow_infer -eq "true")
    {
        # Maybe higher FP - requests longer than avg
        if($resp -like "Unable to connect to the remote server" -and $elapsed1 -gt $global_slowtimeout) #
        {
             Write-Output "[HTTP] $t - open|filtered"
            $ipPortDict[$target] += @($port)
        }

        if($resp -like "The underlying connection was closed: An unexpected error occurred on a receive." -and $elapsed1 -gt $global_slowtimeout)
        {
             Write-Output "[HTTP] $t - open|filtered"
            $ipPortDict[$target] += @($port)
        }

    }

    if($resp.StatusCode)
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }

    if($resp -like "Unable to connect to the remote server" -and $suppressClosedPorts -eq "false")
    {
        Write-Output "[HTTP] $t - closed"
    }
    
    #Write-Output $elapsed1 #uncomment for debugging
    #Write-Output $global_fastTimeout #uncomment for debugging
    if($resp -like "The operation has timed out." -and $elapsed1 -ge $global_timeoutSec)
    {
        Write-Output "[HTTP] $t - open|filtered"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    #May lead to higher FP
    #if($resp -like "The underlying connection was closed: An unexpected error occurred on a receive." -and $elapsted1 -lt $global_fastTimeout)
    #{
    #    Write-Output "[HTTP] $t - open|filtered"
    #    $ipPortDict[$target] += @($port)
    #}
    if($resp -like "The underlying connection was closed: An unexpected error occurred on a receive."  -and $suppressClosedPorts -eq "false" -and $fast_infer -eq "false" -and $slow_infer -eq "false")
    {
        Write-Output "[HTTP] $t - closed"
        #$ipPortDict[$target] += @($port)
    }
    if($resp -like "The underlying connection was closed: An unexpected error occurred on a send.")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "The underlying connection was closed: The connection was closed unexpectedly.")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "The underlying connection was closed: Could not establish trust relationship for the SSL/TLS secure channel.")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "The server committed a protocol violation. Section=ResponseStatusLine")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "The remote server returned an error: (403) Forbidden.")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "The remote server returned an error: (404) Not Found.")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "The plain HTTP request was sent to HTTPS port")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "The remote server returned an error: (400) Bad Request.")
    {
        Write-Output "[HTTP] $t - open"
        $ipPortDict[$target] += @($port)
    }
}

# WSMAN can differentiate some ports, but might miss some.
Function wsman_portscan
{
	Param
    (
         [Parameter(Mandatory=$true, Position=0)]
         [string] $target,
         [Parameter(Mandatory=$true, Position=1)]
         [int] $port
    )
    $t = $target + ":" + $port
    
    try
    {
        #$start = Get-Date # start timing requests
	    $resp = Test-WSMan -ComputerName $target -Port $port -Verbose -UseSSL -ErrorAction Stop
    }
    catch
    {
        $resp = $_.Exception.Message
    }

    # enable for debugging
    #Write-Output $resp
    
    if($resp -like "*2150859046*" -and $suppressClosedPorts -eq "false")
    {
        Write-Output "[WSMAN] $t - Unknown (code: 2150859046)"
        #$ipPortDict[$target] += @($port)
    }
    if($resp -like "*12175*")
    {
        Write-Output "[WSMAN] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "*2150859194*")
    {
        Write-Output "[WSMAN] $t - open"
        $ipPortDict[$target] += @($port)
    }
    if($resp -like "*2150858770*" -and $suppressClosedPorts -eq "false")
    {
        Write-Output "[WSMAN] $t - closed"
    }
    if($resp -like "*12005*")
    {
        Write-Output "[WSMAN] $t - Error"
    }
}

#implement restmethod
# works:
#$response = Invoke-RestMethod -Uri "http://127.0.0.1:443" -Method OPTIONS -Headers @{ Authorization = "aaa" }


foreach ($target in $targetList)
{
    Write-Output ""
    Write-Output "Starting Portscan on $target"
    
    foreach ($port in $portList)
    {

        $c = Get-Random -Minimum 1 -Maximum 4 # random function each time
	    
        if($smtp -eq "true")
        {
            $c = 1 # for full wsman scan
        }
        if($http -eq "true")
        {
            $c = 2 # for full wsman scan
        }
        if($wsman -eq "true")
        {
            $c = 3 # for full wsman scan
        }
        
	    if($c -eq 1)
        {
            smtp_portscan -target $target -port $port
            #$c++
        }
        elseif($c -eq 2)
        {
            http_portscan -target $target -port $port
            #$c++
        }
        elseif($c -eq 3)
        {
            wsman_portscan -target $target -port $port
            #$c++
        }
    }
}

# Display all found ports and ip pairs
#$ipPortDict.GetEnumerator() | Out-String -Width 1000
Write-Output ""
Write-Output "Results below:"
foreach ($entry in $ipPortDict.GetEnumerator()) 
{
    Write-Output "$($entry.Key) : $($entry.Value -join ', ')"
}
