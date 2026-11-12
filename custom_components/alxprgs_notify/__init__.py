"""Expose the Notify call action to Home Assistant."""
import voluptuous as vol
from homeassistant.core import SupportsResponse
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import DOMAIN, NotifyError, call

CONFIG_SCHEMA = cv.config_entry_only_config_schema(DOMAIN)


async def async_setup(hass, config):
    """Register an action targeting the configured Notify entry."""
    async def announce(service_call):
        entries = hass.config_entries.async_loaded_entries(DOMAIN)
        requested = service_call.data.get("config_entry_id")
        matching = [entry for entry in entries if requested is None or entry.entry_id == requested]
        if len(matching) != 1:
            raise HomeAssistantError("Select a loaded ALXPRGS Notify config entry")
        data = matching[0].data
        try:
            return await call(async_get_clientsession(hass), data["url"], data["token"], service_call.data)
        except NotifyError as error:
            if str(error) == "unsupported_tts_language":
                raise ServiceValidationError(
                    "Notify: Use Russian text for speech. Spell names and abbreviations in Cyrillic."
                ) from None
            raise HomeAssistantError(f"Notify: {error}") from None

    hass.services.async_register(DOMAIN, "call", announce, supports_response=SupportsResponse.OPTIONAL,
                                 schema=vol.Schema({
        vol.Required("text"): vol.All(cv.string, vol.Length(min=1, max=300)),
        vol.Optional("title"): vol.All(cv.string, vol.Length(min=1, max=160)),
        vol.Optional("target"): vol.In(("android", "asterisk")),
        vol.Optional("idempotency_key"): vol.All(cv.string, vol.Length(min=8, max=128)),
        vol.Optional("config_entry_id"): cv.string,
    }))
    return True


async def async_setup_entry(hass, entry):
    """Enable the action for a configured entry."""
    return True


async def async_unload_entry(hass, entry):
    """Remove the config entry from the available action targets."""
    return True
