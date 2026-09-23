#!/usr/bin/env python3
"""米家新面板截图：搜索 + 可控属性 + 目标值。

真实的 discover 需要有效 token，这里直接在页面里注入演示数据来渲染界面
（只用于看 UI，不改变任何服务端行为）。
"""
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
OUT = Path("/vol1/1000/workspace/family-message/docs")

URN_PLUG = "urn:miot-spec-v2:device:outlet:0000A002:lfsmt-ls002:1"
URN_LIGHT = "urn:miot-spec-v2:device:light:0000A001:opple-ceiling:1"

SEED = """
(urnPlug, urnLight) => {
  xmStatus = { logged_in: true, expires_in_seconds: 2592000, redirect_url: 'http://homeassistant.local:8123' };
  xmDevices = [
    { name: '书房插座', miot_device_id: 'm1', model: urnPlug,  is_online: true },
    { name: '客厅吸顶灯', miot_device_id: 'm2', model: urnLight, is_online: true },
    { name: '卧室床头灯', miot_device_id: 'm3', model: urnLight, is_online: false },
    { name: '阳台风扇插座', miot_device_id: 'm4', model: urnPlug, is_online: true },
  ];
  specCache[urnPlug] = [
    { siid:2, piid:1, service:'Switch', name:'Switch Status', format:'bool',
      options:[{value:true,label:'开 / 是'},{value:false,label:'关 / 否'}], range:null, unit:null },
  ];
  specCache[urnLight] = [
    { siid:2, piid:1, service:'Light', name:'Switch Status', format:'bool',
      options:[{value:true,label:'开 / 是'},{value:false,label:'关 / 否'}], range:null, unit:null },
    { siid:2, piid:3, service:'Light', name:'Brightness', format:'uint8',
      options:[], range:[0,100,1], unit:'percentage' },
    { siid:2, piid:5, service:'Light', name:'Color Temperature', format:'uint32',
      options:[], range:[1000,10000,1], unit:'kelvin' },
  ];
  xmBoundList = [
    { id:1, name:'书房插座', urn: urnPlug, miot_device_id:'m1', power_siid:2, power_piid:1,
      power_value:'false', power_action:'on', target_device_id:'pc_demo' },
    { id:2, name:'客厅吸顶灯', urn: urnLight, miot_device_id:'m2', power_siid:2, power_piid:3,
      power_value:'50', power_action:'on', target_device_id:'pc_demo' },
  ];
  document.getElementById('xm-login').hidden = true;
  document.getElementById('xm-authed').hidden = false;
  document.getElementById('xm-state-line').textContent =
    '已授权米家。绑定规则：米家设备 + 属性/值 → 某台 PC（只影响那台 PC 上的「开机」按钮）';
  document.getElementById('xm-login-info').textContent = 'token 还剩约 30 天（到期前自动续期）';
  renderXmForm();
  renderXmBound();
}
"""


def main() -> None:
    with sync_playwright() as pw:
        b = pw.chromium.launch()
        for mode in ("dark", "light"):
            page = b.new_page(viewport={"width": 1440, "height": 1000}, locale="zh-CN")
            page.goto(BASE, wait_until="networkidle")
            page.evaluate("m => localStorage.setItem('fm.mode', m)", mode)
            page.reload(wait_until="networkidle")
            page.wait_for_timeout(600)
            page.click("#btn-settings")
            page.wait_for_timeout(600)
            page.evaluate("([a,b]) => (" + SEED + ")(a,b)", [URN_PLUG, URN_LIGHT])
            page.wait_for_timeout(900)
            # 滚到米家区域
            page.eval_on_selector("#xm-authed", "e => e.scrollIntoView({block:'center'})")
            page.wait_for_timeout(400)
            p = OUT / f"md3-xiaomi-{mode}.png"
            page.screenshot(path=str(p))
            print("  ", p.name)

            if mode == "dark":
                # 搜索过滤演示
                page.fill("#xm-search", "灯")
                page.wait_for_timeout(700)
                p2 = OUT / "md3-xiaomi-search.png"
                page.screenshot(path=str(p2))
                print("  ", p2.name)
            page.close()
        b.close()


if __name__ == "__main__":
    main()
