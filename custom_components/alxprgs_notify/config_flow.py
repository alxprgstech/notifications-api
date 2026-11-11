"""Configure Notify with a dedicated announcement source."""
import voluptuous as vol
from homeassistant import config_entries
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.selector import TextSelector, TextSelectorConfig, TextSelectorType

from .api import DOMAIN, NotifyError, request, validate_url


class NotifyConfigFlow(config_entries.ConfigFlow, domain=DOMAIN):
    """Verify a Notify origin and its announcement permission."""

    VERSION = 1

    async def async_step_user(self, user_input=None):
        errors = {}
        if user_input is not None:
            try:
                url = validate_url(user_input["url"].strip())
                token = user_input["token"].strip()
                info = await request(async_get_clientsession(self.hass), url, token, "GET", "session")
                actor = info.get("actor", {})
                if actor.get("kind") != "integration" or "calls:announce" not in actor.get("scopes", []):
                    raise NotifyError("invalid_auth")
                await self.async_set_unique_id(url)
                self._abort_if_unique_id_configured()
                return self.async_create_entry(title="ALXPRGS Notify", data={"url": url, "token": token})
            except ValueError:
                errors["base"] = "invalid_url"
            except NotifyError as error:
                errors["base"] = "invalid_auth" if str(error) == "invalid_auth" else "cannot_connect"
        return self.async_show_form(step_id="user", errors=errors, data_schema=vol.Schema({
            vol.Required("url"): str,
            vol.Required("token"): TextSelector(TextSelectorConfig(type=TextSelectorType.PASSWORD)),
        }))
