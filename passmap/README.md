## help
```
usage: passmap.py [-h] -i INTERFACE [-f FILTER] [-A]

Dynamic sniffer with grouped display, filtering, and full protocol logging

options:
  -h, --help            show this help message and exit
  -i INTERFACE, --interface INTERFACE
                        Interface to sniff on
  -f FILTER, --filter FILTER
                        Filter IPs starting with prefixes, e.g., ":192.168,:10.0"
  -A, --all             Include all protocols and malformed traffic
```

## Quick start
``sudo python3 passmap.p -i <interface> -f ":192",":10."``
