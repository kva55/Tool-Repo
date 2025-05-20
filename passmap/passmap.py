#!/usr/bin/env python3

from scapy.all import sniff, ARP, IP, IPv6, TCP, UDP, ICMP, Ether, get_if_addr
import argparse
from collections import defaultdict
import csv
import os
import time
from datetime import datetime

seen = set()
ip_ports = defaultdict(list)
last_draw = 0
prefix_filters = []
log_path = ""
csv_path = ""
include_all_protocols = False  # Controlled by -A

def write_entry(identifier, label):
    with open(log_path, "a") as logfile:
        logfile.write(f"{identifier}:{label}\n")
        logfile.flush()
    with open(csv_path, "a", newline='') as csvfile:
        writer = csv.writer(csvfile)
        writer.writerow([identifier, label])
        csvfile.flush()

def ip_allowed(ip):
    if not prefix_filters:
        return True
    for prefix in prefix_filters:
        if ip.startswith(prefix):
            return True
    return False

def redraw_screen():
    os.system('clear')
    print("📡 Unique Source IPs/MACs and Ports - Live View\n")

    identifiers = list(ip_ports.keys())
    print(f"Unique Sources Seen (Total: {len(identifiers)})")
    for ident in identifiers:
        print(ident)
    print("\n" + "-" * 60 + "\n")

    for ident in identifiers:
        print(f"{ident}:")
        for label in ip_ports[ident]:
            print(f"  - {label}")
        print()

def packet_handler(pkt):
    global last_draw
    now = time.time()

    # ARP / RARP
    if ARP in pkt:
        ip_src = pkt[ARP].psrc
        op = pkt[ARP].op
        label = "ARP" if op in (1, 2) else "RARP"
        key = (ip_src, label)

        if ip_src and ip_src != host_ip and ip_allowed(ip_src) and key not in seen:
            seen.add(key)
            ip_ports[ip_src].append(label)
            write_entry(ip_src, label)

    # IPv4
    elif IP in pkt:
        ip_src = pkt[IP].src
        if ip_src == host_ip or not ip_allowed(ip_src):
            return

        if ICMP in pkt:
            label = f"ICMP: type {pkt[ICMP].type} code {pkt[ICMP].code}"
        elif TCP in pkt:
            label = f"TCP: {pkt[TCP].dport}"
        elif UDP in pkt:
            label = f"UDP: {pkt[UDP].dport}"
        else:
            label = "IP-OTHER"

        key = (ip_src, label)
        if key not in seen:
            seen.add(key)
            ip_ports[ip_src].append(label)
            write_entry(ip_src, label)

    # IPv6
    elif IPv6 in pkt:
        ip_src = pkt[IPv6].src
        if ip_src == host_ip or not ip_allowed(ip_src):
            return

        label = "IPv6"
        key = (ip_src, label)
        if key not in seen:
            seen.add(key)
            ip_ports[ip_src].append(label)
            write_entry(ip_src, label)

    # Everything else (if -A is enabled)
    elif include_all_protocols and Ether in pkt:
        src_mac = pkt[Ether].src
        proto_summary = pkt.summary()
        label = f"OTHER: {proto_summary}"
        key = (src_mac, label)

        if key not in seen:
            seen.add(key)
            ip_ports[src_mac].append(label)
            write_entry(src_mac, label)

    # Redraw terminal every 0.5 seconds
    if now - last_draw > 0.5:
        redraw_screen()
        last_draw = now

def main():
    global host_ip, prefix_filters, log_path, csv_path, include_all_protocols

    parser = argparse.ArgumentParser(description="Dynamic sniffer with grouped display, filtering, and full protocol logging")
    parser.add_argument("-i", "--interface", required=True, help="Interface to sniff on")
    parser.add_argument("-f", "--filter", help='Filter IPs starting with prefixes, e.g., ":192.168,:10.0"', default="")
    parser.add_argument("-A", "--all", action="store_true", help="Include all protocols and malformed traffic")

    args = parser.parse_args()
    host_ip = get_if_addr(args.interface)
    include_all_protocols = args.all

    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    log_path = f"unique_ips_{timestamp}.log"
    csv_path = f"unique_ips_{timestamp}.csv"

    print(f"[+] Host IP on {args.interface}: {host_ip}")
    if args.filter:
        prefix_filters = [entry[1:] for entry in args.filter.split(',') if entry.startswith(':')]
        print(f"[+] IP prefix filters applied: {prefix_filters}")
    if include_all_protocols:
        print(f"[+] All protocol logging enabled (-A)")

    # Write CSV header if new
    if not os.path.exists(csv_path):
        with open(csv_path, "w", newline='') as csvfile:
            writer = csv.writer(csvfile)
            writer.writerow(["Source", "Label/Port"])

    sniff(iface=args.interface, prn=packet_handler, store=False)

if __name__ == "__main__":
    main()