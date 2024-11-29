## Using ppscanner
basic port scan - uses random protocols

``./ppscanner.ps1 --target 192.168.0.1 --port 22,23,443``

Multiple ips - use comma delimiter

``./ppscanner.ps1 --target 192.168.0.1,192.168.0.2 --port 22,23,443``

Using time-based inference
- Can specify time for quick and slow responses (default fast-timeout=1 second, slow-timeout=15)

``./ppscanner.ps1 --target 192.168.0.1,192.168.0.2 --port 22,23,443 --fast-timeout 5 --slow-timeout 30``

mail only scan

``./ppscanner.ps1 --target 192.168.0.1 --port 22,23,443 -smtp``

http only scan

``./ppscanner.ps1 --target 192.168.0.1 --port 22,23,443 -http``

wsman only scan

``./ppscanner.ps1 --target 192.168.0.1 --port 22,23,443 -wsman``

Cloudflare ports check (Use a mix for http:// and https:// for http-based scans)

``./ppscanner.ps1 --target 192.168.0.1 --cloudflare-check``

``./ppscanner.ps1 --target https://192.168.0.1 --cloudflare-check``

``./ppscanner.ps1 --target http://192.168.0.1 --cloudflare-check``

Show closed ports

``./ppscanner.ps1 --target 192.168.0.1 --closed``

Scan all ports

```
./ppscanner.ps1 --target 192.168.0.1 -p-
./ppscanner.ps1 --target 192.168.0.1 -all-ports
```

## Notes about functions
- wsman: fastest option, but doesn't detect standard ports such as 21, 25, 110, 143, etc. Also misses ports like 80 and 135.
- http: most accurate but timeout needs to be set to acceptable value (default 30 seconds, otherwise hangs)
- rest: Similar to http but different function
- mail: slowest option, not as verbose as http but detects ports
- random: makes scan time random, but can miss some ports, likely better to use this option to avoid edr
