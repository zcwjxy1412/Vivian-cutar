# VIvian OneDragon v2.6.0 desktop handoff
# The packaged launcher imports this module. Ordinary OneDragon launches use its original entry.
import os as _vivian_os


def _vivian_handoff(self, args):
    run_id = _vivian_os.environ.get("VIVIAN_ZZZ_RUN")
    if not run_id:
        return ApplicationLauncher.run_application(self, args)

    import ctypes
    import time
    import re
    import unicodedata
    from one_dragon.base.operation.operation_base import OperationResult
    from zzz_od.application.one_dragon_app.zzz_one_dragon_app import ZOneDragonApp
    from zzz_od.operation.enter_game.enter_game import EnterGame

    instance = int(args.instance)
    pid = int(_vivian_os.environ["VIVIAN_ZZZ_GAME_PID"])
    expected = _vivian_os.environ["VIVIAN_ZZZ_APPS"].split(",")
    masked = _vivian_os.environ["VIVIAN_ZZZ_MASK"]
    ctx = self.ctx
    outcome = "failed"
    results = {}
    original_execute = ZOneDragonApp.execute

    def emit(*fields):
        print("|".join(("VIVIAN-ZZZ", run_id, *map(str, fields))), flush=True)

    def run_from_login(app):
        hwnd = ctx.controller.game_win._hWnd
        owner = ctypes.c_ulong()
        if not hwnd:
            return OperationResult(success=False, status="Vivian game window unavailable")
        ctypes.windll.user32.GetWindowThreadProcessId(ctypes.c_void_p(hwnd), ctypes.byref(owner))
        if owner.value != pid:
            return OperationResult(success=False, status="Vivian game window changed")

        class EnterSavedAccount(EnterGame):
            confirmed = False

            def check_enter_click_status_text(self, screen, include_enter_click=False):
                if not self.confirmed:
                    return None, None
                return super().check_enter_click_status_text(screen, include_enter_click)

            def check_login_related(self, screen):
                if not self.confirmed:
                    header = screen[444:535, 610:1195]
                    identities = []
                    for text in self.ctx.ocr.run_ocr(header):
                        for prefix, suffix in re.findall(
                            r"(?<!\d)(\d{3})\s*[*•·]{3,}\s*(\d{2,4})(?!\d)", unicodedata.normalize("NFKC", text)
                        ):
                            identities.append(prefix + "****" + suffix)
                    if identities != [masked]:
                        emit("detail", "login_changed")
                        return self.round_fail("登录确认卡片的脱敏账号发生变化")
                    result = self.round_by_find_and_click_area(screen, "打开游戏", "账号确认-进入游戏")
                    if result.is_success:
                        self.confirmed = True
                        return self.round_wait("账号已确认", wait=1)
                    emit("detail", "confirmation_missing")
                    return self.round_fail("未找到已保存账号确认按钮")
                if self.round_by_find_area(screen, "打开游戏", "点击进入游戏").is_success:
                    return self.round_success("点击进入游戏", wait=1)
                return self.round_fail("未识别已保存账号确认画面")

        enter = EnterSavedAccount(ctx)
        enter.force_login = False
        if not enter.execute().success:
            emit("detail", "entry_failed")
            return OperationResult(success=False, status="Vivian saved account entry failed")
        emit("entered")
        return original_execute(app)

    try:
        emit("start", _vivian_os.getpid(), instance)
        deadline = time.monotonic() + 120
        while not ctx.ready_for_application:
            if time.monotonic() >= deadline:
                return
            time.sleep(0.2)
        if [row.idx for row in ctx.one_dragon_config.instance_list_in_od] != [instance]:
            return
        if ctx.current_instance_idx != instance:
            ctx.switch_instance(instance)
        config = ctx.app_group_manager.get_one_dragon_group_config(instance)
        if [row.app_id for row in config.app_list if row.enabled] != expected:
            return

        get_app = ctx.run_context.get_application

        def track_app(app_id, instance_idx, group_id):
            app = get_app(app_id, instance_idx, group_id)
            if app_id in expected and instance_idx == instance and group_id == "one_dragon" and app is not None:
                execute = app.execute

                def track_execution():
                    result = execute()
                    status = "success" if result.success else "failed"
                    results[app_id] = status
                    emit("task", app_id, status)
                    return result

                app.execute = track_execution
            return app

        ctx.run_context.get_application = track_app
        ZOneDragonApp.execute = run_from_login
        result = ctx.run_context.run_application("one_dragon", instance, "one_dragon")
        if str(result.finish_reason) == "COMPLETED" and all(status == "success" for status in results.values()):
            outcome = "success" if all(app in results for app in expected) else "incomplete"
    finally:
        ZOneDragonApp.execute = original_execute
        ctx.after_app_shutdown()
        emit("end", outcome)


ZApplicationLauncher.run_application = _vivian_handoff
