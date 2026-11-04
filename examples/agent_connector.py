"""Reference outbound agent connector. The callback is passed explicitly; nothing is imported from the network."""
import asyncio
import os
from alxprgs_notify import AgentConnector, CallbackRuntime


async def handle(command):
    # The agent environment decides which registered task (if any) this command refers to.
    # Raising here leaves the durable command unacknowledged so Notify retries it later.
    print("Получена команда", command["eventId"], command["type"], command.get("payload"))


async def main():
    connector = AgentConnector(
        os.environ["NOTIFY_URL"],
        os.environ["NOTIFY_TOKEN"],
        agent_id=os.environ["NOTIFY_AGENT_ID"],
        name=os.getenv("NOTIFY_AGENT_NAME", "Agent host"),
        capabilities=["text_tasks"],
        sessions=[{"id": "demo", "name": "Demo task"}],
        runtime=CallbackRuntime(handle),
    )
    await connector.run()


if __name__ == "__main__":
    asyncio.run(main())
