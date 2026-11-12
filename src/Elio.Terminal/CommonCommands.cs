namespace Elio.Terminal;

/// <summary>
/// Commands most people who use a terminal type sooner or later. They are offered after the user's own, so that
/// suggestions are not empty on the first day or on a server never visited before.
/// </summary>
public static class CommonCommands
{
    public static readonly string[] All =
    [
        "ls -la", "ls -lah", "ls -ltr", "cd ..", "cd ~", "cd -", "pwd", "clear", "exit", "history",
        "cat /etc/os-release", "tail -f /var/log/syslog", "tail -n 100 ", "less ", "head -n 20 ",
        "grep -rn \"\" .", "grep -i ", "find . -name \"\"", "find . -type f -mtime -1", "which ", "whoami", "hostname", "uname -a", "uptime", "date",
        "df -h", "du -sh *", "du -sh .", "free -h", "top", "htop", "ps aux", "ps aux | grep ", "kill -9 ", "pkill ", "lsblk", "mount", "env", "export ",
        "mkdir -p ", "rm -rf ", "cp -r ", "mv ", "touch ", "chmod +x ", "chmod 644 ", "chmod 755 ", "chown -R ", "ln -s ",
        "tar -xzf ", "tar -czf ", "unzip ", "zip -r ", "curl -I ", "curl -fsSL ", "wget ", "ping -c 4 ", "traceroute ", "dig ", "nslookup ",
        "ss -tulpn", "netstat -tulpn", "ip a", "ip r", "ifconfig", "nc -zv ", "ssh ", "scp ", "rsync -avz ",
        "sudo -i", "sudo su -", "sudo !!", "su - ",
        "systemctl status ", "systemctl restart ", "systemctl start ", "systemctl stop ", "systemctl enable --now ", "systemctl daemon-reload", "systemctl list-units --failed",
        "journalctl -u ", "journalctl -xe", "journalctl -f", "journalctl -u  -f",
        "apt update", "apt upgrade -y", "apt install -y ", "apt remove ", "apt autoremove -y", "dnf install -y ", "dnf update -y", "yum install -y ", "yum update -y", "pacman -Syu", "apk add ",
        "docker ps", "docker ps -a", "docker images", "docker logs -f ", "docker exec -it  sh", "docker exec -it  bash", "docker restart ", "docker stop ", "docker rm ", "docker rmi ",
        "docker compose up -d", "docker compose down", "docker compose logs -f", "docker compose pull", "docker compose ps", "docker system prune -af", "docker stats",
        "kubectl get pods", "kubectl get pods -A", "kubectl get nodes", "kubectl get svc", "kubectl describe pod ", "kubectl logs -f ", "kubectl apply -f ", "kubectl delete pod ", "kubectl exec -it  -- sh", "kubectl config get-contexts",
        "git status", "git pull", "git push", "git fetch --all", "git log --oneline -20", "git diff", "git add .", "git commit -m \"\"", "git checkout ", "git checkout -b ", "git branch -a", "git stash", "git stash pop", "git merge ", "git rebase ", "git reset --hard", "git clone ",
        "nginx -t", "nginx -s reload", "crontab -e", "crontab -l", "ufw status", "ufw allow ", "firewall-cmd --list-all", "iptables -L -n",
        "vim ", "nano ", "vi ", "tmux", "tmux attach", "tmux -CC attach", "screen -r", "watch -n 1 ", "nohup  &", "reboot", "shutdown -h now",
        "python3 ", "python3 -m venv .venv", "pip install ", "pip install -r requirements.txt", "node ", "npm install", "npm run ", "npm start", "dotnet build", "dotnet run", "dotnet restore", "make", "make install",
        "mysql -u root -p", "psql -U postgres", "redis-cli", "openssl s_client -connect ", "openssl x509 -noout -text -in ", "certbot renew",
        "Get-ChildItem", "Get-Process", "Get-Service", "Restart-Service ", "Get-Content  -Tail 50 -Wait", "Set-Location ", "Test-NetConnection ", "ipconfig /all", "netstat -ano", "tasklist", "winget install ", "winget upgrade --all",
    ];
}
