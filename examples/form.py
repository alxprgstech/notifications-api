"""Explicit form request example. Values are produced only by the owner's phone."""
import os
import uuid
from datetime import datetime, timedelta, timezone
from alxprgs_notify import NotifyClient

fields = [
    {"id": "env", "label": "Среда", "type": "single_choice", "required": True,
     "options": [{"id": "staging", "label": "Staging"}, {"id": "prod", "label": "Prod"}]},
    {"id": "count", "label": "Количество", "type": "number", "required": True, "min": 1, "max": 10},
    {"id": "dry", "label": "Dry run", "type": "boolean", "required": True},
    {"id": "note", "label": "Комментарий", "type": "text", "maxLength": 2000},
]
with NotifyClient(os.environ["NOTIFY_URL"], os.environ["NOTIFY_TOKEN"]) as client:
    request = client.request_form(
        "Параметры выкладки",
        "Проверьте значения перед запуском. Форма не запускает действие автоматически.",
        expires_at=(datetime.now(timezone.utc) + timedelta(minutes=10)).isoformat(),
        fields=fields,
        idempotency_key=str(uuid.uuid4()),
        security_level=os.getenv("NOTIFY_LEVEL", "standard"),
    )
    print("Ожидается ответ на", request["id"])
    result = client.wait(request["id"], timeout=300)
    if result["status"] == "answered":
        print("Ответ формы:", (result.get("formAnswer") or {}).get("values"))
    else:
        print("Форма не заполнена:", result["status"])
