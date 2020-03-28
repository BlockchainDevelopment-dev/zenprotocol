# Run zen-node as a daemon (Linux only) 

1. Install nodejs version 8
2. Install lmdb from package manager
3. Run

```
npm config set @bdl:registry https://www.myget.org/F/blockchaindevelopment/npm/
npm install @bdl/zen-wallet -g
```
4. Create new file at `/etc/systemd/system/zenprotocol.service` and copy the content of `zenprotocol.service` to the new file.
5. Run 

```
sudo systemctl daemon reload
sudo systemctl enable zenprotocol.service
sudo systemctl start zenprotocol.service
```
