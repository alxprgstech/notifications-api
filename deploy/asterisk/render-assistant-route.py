"""Render a trusted phone-assistant dialplan fragment without changing Asterisk."""

import argparse
import ipaddress
import re


def render(extension, phone):
    if re.fullmatch(r"[0-9]{2,8}", extension) is None:
        raise ValueError("invalid extension")
    address = ipaddress.IPv4Address(phone)
    lan_ranges = ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")
    if not any(address in ipaddress.IPv4Network(network) for network in lan_ranges):
        raise ValueError("expected an RFC 1918 phone address")
    return (
        f"exten => {extension},1,Set(NOTIFY_SIP_REMOTE=${{CHANNEL(pjsip,remote_addr)}})\n"
        f' same => n,GotoIf($["${{CUT(NOTIFY_SIP_REMOTE,:,1)}}" = "{address}"]?trusted)\n'
        " same => n,Hangup(21)\n"
        f" same => n(trusted),Stasis(alxprgs-notify,phone-assistant,{extension})\n"
        " same => n,Hangup()"
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("extension")
    parser.add_argument("phone_ipv4")
    inputs = parser.parse_args()
    try:
        print(render(inputs.extension, inputs.phone_ipv4))
    except ValueError as error:
        parser.error(str(error))
