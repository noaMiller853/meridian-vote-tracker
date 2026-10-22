#!/bin/sh
# Runs before nginx starts (nginx image: /docker-entrypoint.d). Writes the DNS server of this container into a file that
# nginx.conf includes, so the API name is looked up when a request arrives instead of when nginx starts.
set -e
ns=$(awk '$1=="nameserver" { if ($2 ~ ":") print "[" $2 "]"; else print $2 }' /etc/resolv.conf | tr '\n' ' ')
[ -n "$ns" ] || ns="127.0.0.11"
echo "resolver $ns valid=10s ipv6=off;" > /etc/nginx/resolver.conf
echo "05-resolver.sh: $(cat /etc/nginx/resolver.conf)"
