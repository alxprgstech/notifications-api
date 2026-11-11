import asyncio
import sys
import unittest
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


class McpTests(unittest.IsolatedAsyncioTestCase):
    async def test_official_stdio_client_discovers_tools(self):
        async def scenario():
            async with stdio_client(StdioServerParameters(command=sys.executable, args=["-m", "alxprgs_notify.mcp_stdio"])) as streams:
                async with ClientSession(streams[0], streams[1]) as session:
                    await session.initialize()
                    result = await session.list_tools()
                    self.assertEqual({t.name for t in result.tools}, {"send_notification", "request_confirmation", "request_form", "get_request", "wait_for_response", "cancel_request", "start_call", "start_conversation", "get_call_status"})
        await asyncio.wait_for(scenario(), timeout=25)


if __name__ == "__main__":
    unittest.main()
