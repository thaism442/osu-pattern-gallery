#!/bin/bash
# osu! Pattern Gallery - bot server setup for an Oracle Cloud (Ubuntu) VM.
# Paste this whole file into "Cloud-init script" when creating the VM.
# Only change the two lines below: the bot's osu! name and its IRC password
# (osu.ppy.sh -> Account settings -> Legacy API -> IRC).
IRC_USER="exporage"
IRC_PASS="BURAYA_IRC_SIFRESI"

# ---------------------------------------------------------------- nothing to change below
exec > /var/log/patterngallery-install.log 2>&1
set +x   # never print the password into the log
export DEBIAN_FRONTEND=noninteractive
REPO="https://raw.githubusercontent.com/thaism442/osu-pattern-gallery/main"

apt-get update
apt-get install -y python3-venv curl gnupg debian-keyring debian-archive-keyring apt-transport-https iptables-persistent

# Caddy: web server that gets a free https certificate by itself
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' -o /etc/apt/sources.list.d/caddy-stable.list
apt-get update
apt-get install -y caddy

# the bot server
useradd --system --home-dir /var/lib/patterngallery --create-home --shell /usr/sbin/nologin pg || true
mkdir -p /opt/patterngallery
python3 -m venv /opt/patterngallery/venv
/opt/patterngallery/venv/bin/pip install "websockets>=13"
curl -fsSL "$REPO/server/server.py" -o /opt/patterngallery/server.py

printf 'IRC_USER=%s\nIRC_PASS=%s\nDATA_FILE=/var/lib/patterngallery/data.json\n' "$IRC_USER" "$IRC_PASS" > /etc/patterngallery.env
chmod 600 /etc/patterngallery.env

cat > /etc/systemd/system/patterngallery.service <<'EOF'
[Unit]
Description=osu! Pattern Gallery bot server
After=network-online.target
Wants=network-online.target

[Service]
User=pg
EnvironmentFile=/etc/patterngallery.env
ExecStart=/opt/patterngallery/venv/bin/python /opt/patterngallery/server.py
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# "pg-update": downloads the newest server.py from GitHub and restarts
cat > /usr/local/bin/pg-update <<EOF
#!/bin/bash
curl -fsSL "$REPO/server/server.py" -o /opt/patterngallery/server.py && systemctl restart patterngallery && echo updated
EOF
chmod +x /usr/local/bin/pg-update

# open ports 80 and 443 (Oracle's Ubuntu image blocks everything except ssh)
for port in 443 80; do
  line=$(iptables -L INPUT --line-numbers | awk '/REJECT/ {print $1; exit}')
  if [ -n "$line" ]; then iptables -I INPUT "$line" -p tcp --dport $port -m state --state NEW -j ACCEPT
  else iptables -A INPUT -p tcp --dport $port -m state --state NEW -j ACCEPT; fi
done
netfilter-persistent save

# free domain name from the public ip: 1.2.3.4 -> 1-2-3-4.sslip.io
IP=$(curl -fsS https://api.ipify.org || curl -fsS https://ifconfig.me)
DOMAIN="$(echo "$IP" | tr . -).sslip.io"
cat > /etc/caddy/Caddyfile <<EOF
$DOMAIN {
    reverse_proxy 127.0.0.1:8765
}
EOF

systemctl daemon-reload
systemctl enable --now patterngallery
systemctl restart caddy
echo "wss://$DOMAIN/ws" > /root/server-url.txt
echo "DONE: wss://$DOMAIN/ws"
