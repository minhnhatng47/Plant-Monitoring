# Web v2 refactor v1.1.0-rpi-topic-v2

Files:
- backend/main.py
- backend/.env.example
- backend/requirements.txt
- frontend/src/App.jsx
- frontend/src/App.css
- systemd/plant-backend.service

Apply on Raspberry Pi:

```bash
cd ~/Plant-Monitoring-CPS

cp /mnt/data/web_v2_refactor/backend/main.py Web/web_v2/backend/main.py
cp /mnt/data/web_v2_refactor/backend/.env.example Web/web_v2/backend/.env.example
cp /mnt/data/web_v2_refactor/backend/requirements.txt Web/web_v2/backend/requirements.txt
cp /mnt/data/web_v2_refactor/frontend/src/App.jsx Web/web_v2/frontend/src/App.jsx
cp /mnt/data/web_v2_refactor/frontend/src/App.css Web/web_v2/frontend/src/App.css
```

Create backend .env:

```bash
cd ~/Plant-Monitoring-CPS/Web/web_v2/backend
cp .env.example .env
nano .env
```

Install backend:

```bash
python3 -m venv env
source env/bin/activate
pip install --upgrade pip
pip install -r requirements.txt
uvicorn main:app --host 0.0.0.0 --port 8000
```

Install systemd service:

```bash
sudo cp /mnt/data/web_v2_refactor/systemd/plant-backend.service /etc/systemd/system/plant-backend.service
sudo systemctl daemon-reload
sudo systemctl enable plant-backend.service
sudo systemctl start plant-backend.service
sudo systemctl status plant-backend.service --no-pager
journalctl -u plant-backend.service -f
```

Install frontend:

```bash
cd ~/Plant-Monitoring-CPS/Web/web_v2/frontend
rm -rf node_modules package-lock.json
npm install
npm run dev -- --host 0.0.0.0
```

Test backend:

```bash
curl http://127.0.0.1:8000/api/health
curl http://127.0.0.1:8000/api/dashboard/latest
```

Test command API:

```bash
curl -X POST http://127.0.0.1:8000/api/command/light \
  -H 'Content-Type: application/json' \
  -d '{"state":"ON","duration_s":300,"reason":"cli_test","source":"curl"}'
```
