"""An explicit reference integration, not an arbitrary task runner."""
import os
import uuid
from datetime import datetime, timedelta, timezone
from alxprgs_notify import NotifyClient

with NotifyClient(os.environ["NOTIFY_URL"], os.environ["NOTIFY_TOKEN"]) as client:
    request = client.request("Разрешить тестовое действие?", "Агент-пример продолжит демонстрацию после вашего ответа. Никакие внешние действия не выполняются.", expires_at=(datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat(), idempotency_key=str(uuid.uuid4()), security_level=os.getenv("NOTIFY_LEVEL", "standard"))
    print("Ожидается ответ на", request["id"])
    result = client.wait(request["id"], timeout=300)
    decision = (result.get("answer") or {}).get("decision")
    if result["status"] == "answered" and decision == "yes":
        print("Получено явное согласие на тестовое действие.")
    else:
        print("Действие не разрешено:", result["status"], decision)
