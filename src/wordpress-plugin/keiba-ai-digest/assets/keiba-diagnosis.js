/* 今日の狙い目レース診断（スマホ優先）。質問は1画面1問、回答はタップだけ。 */
(function () {
    'use strict';

    var cfg = window.KEIBA_DIAG;
    var root = document.getElementById('keiba-diagnosis');
    if (!cfg || !root) {
        return;
    }

    var QUESTIONS = [
        {
            key: 's',
            title: '今日の競馬で重視したいのは？',
            options: [['hit', '的中率を重視'], ['ana', '穴馬を狙いたい'], ['high', '高配当を狙いたい'], ['ai', 'AIの総合評価を重視']]
        },
        {
            key: 'p',
            title: 'どのような馬を狙いたいですか？',
            options: [['fav', '人気馬中心'], ['mid', '中穴中心'], ['long', '大穴中心'], ['any', '特にこだわらない']]
        },
        {
            key: 'm',
            title: '重視する予想材料は？',
            options: [['pace', '展開・先行力'], ['agari', '上がり・末脚'], ['jockey', '騎手・コース適性'], ['pedigree', '血統・調教'], ['ai', 'AI総合指数']]
        },
        {
            key: 'c',
            title: '今日予想したい競馬は？',
            options: [['central', '中央競馬'], ['local', '地方競馬'], ['both', 'どちらでもよい']]
        }
    ];

    var STORAGE_KEY = 'keiba_diag_last';
    var answers = {};
    var step = 0;

    /* ---------------------------------------------------------------- 計測 */

    function queryParam(name) {
        var match = new RegExp('[?&]' + name + '=([^&]*)').exec(window.location.search);
        return match ? decodeURIComponent(match[1].replace(/\+/g, ' ')) : '';
    }

    function referrerHost() {
        try {
            return document.referrer ? new URL(document.referrer).hostname : '';
        } catch (e) {
            return '';
        }
    }

    function track(name) {
        var gaName = 'diagnosis_' + name;
        try {
            if (typeof window.gtag === 'function') {
                window.gtag('event', gaName, { event_category: 'diagnosis' });
            } else {
                // WP Rocket等でGA4のタグの読み込みが遅らされている場合に備え、gtag形式（arguments）でキューへ積む。
                // タグが読み込まれた時点で順に処理される。
                window.dataLayer = window.dataLayer || [];
                (function () { window.dataLayer.push(arguments); })('event', gaName, { event_category: 'diagnosis' });
            }
            if (window.dataLayer && typeof window.dataLayer.push === 'function') {
                window.dataLayer.push({ event: gaName }); // Googleタグマネージャー用
            }
        } catch (e) { /* 計測の失敗で診断を止めない */ }

        try {
            var body = JSON.stringify({ event: name, utm: queryParam('utm_source'), ref: referrerHost() });
            if (navigator.sendBeacon) {
                navigator.sendBeacon(cfg.eventUrl, new Blob([body], { type: 'application/json' }));
            } else {
                var xhr = new XMLHttpRequest();
                xhr.open('POST', cfg.eventUrl, true);
                xhr.setRequestHeader('Content-Type', 'application/json');
                xhr.send(body);
            }
        } catch (e) { /* 同上 */ }
    }

    /* ---------------------------------------------------------------- 描画の補助 */

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined && text !== null) {
            node.textContent = text;
        }
        return node;
    }

    function clear() {
        while (root.firstChild) {
            root.removeChild(root.firstChild);
        }
    }

    function button(label, className, onClick) {
        var b = el('button', className, label);
        b.type = 'button';
        b.addEventListener('click', onClick);
        return b;
    }

    function disclaimer() {
        return el('p', 'kdg-disclaimer', cfg.disclaimer);
    }

    function scrollTop() {
        try {
            root.scrollIntoView({ block: 'start' });
        } catch (e) { /* 古いブラウザは無視 */ }
    }

    /* ---------------------------------------------------------------- 画面 */

    function showIntro() {
        clear();
        var box = el('div', 'kdg-card kdg-intro');
        box.appendChild(el('p', 'kdg-catch', '今日、狙うならどのレース？'));
        box.appendChild(el('h2', 'kdg-title', '30秒でわかる無料競馬診断'));
        box.appendChild(el('p', 'kdg-lead', '4つの質問に答えるだけ。AI指数のデータから、これから発走するレースの中で、あなたに合った注目レースをご紹介します。'));
        box.appendChild(button('無料診断スタート', 'kdg-btn kdg-btn-primary', function () {
            track('start');
            answers = {};
            step = 0;
            showQuestion();
        }));
        box.appendChild(disclaimer());

        var saved = loadSaved();
        if (saved) {
            box.appendChild(button('前回の診断結果を見る', 'kdg-btn kdg-btn-link', function () {
                answers = saved;
                runDiagnosis(true);
            }));
        }
        root.appendChild(box);
    }

    function showQuestion() {
        clear();
        var q = QUESTIONS[step];
        var box = el('div', 'kdg-card');

        var progress = el('div', 'kdg-progress');
        var bar = el('div', 'kdg-progress-bar');
        bar.style.width = ((step + 1) / QUESTIONS.length * 100) + '%';
        progress.appendChild(bar);
        box.appendChild(progress);
        box.appendChild(el('p', 'kdg-step', '質問 ' + (step + 1) + ' / ' + QUESTIONS.length));
        box.appendChild(el('h2', 'kdg-question', q.title));

        var list = el('div', 'kdg-options');
        q.options.forEach(function (option) {
            list.appendChild(button(option[1], 'kdg-btn kdg-option', function () {
                answers[q.key] = option[0];
                if (step + 1 < QUESTIONS.length) {
                    step += 1;
                    showQuestion();
                } else {
                    runDiagnosis();
                }
            }));
        });
        box.appendChild(list);

        if (step > 0) {
            box.appendChild(button('← 前の質問に戻る', 'kdg-btn kdg-btn-link', function () {
                step -= 1;
                showQuestion();
            }));
        }
        root.appendChild(box);
        scrollTop();
    }

    function showMessage(title, text, withRetry) {
        clear();
        var box = el('div', 'kdg-card');
        box.appendChild(el('h2', 'kdg-title', title));
        box.appendChild(el('p', 'kdg-lead', text));
        if (withRetry) {
            box.appendChild(button('条件を変えてもう一度診断する', 'kdg-btn kdg-btn-primary', showIntro));
        }
        box.appendChild(disclaimer());
        root.appendChild(box);
        scrollTop();
    }

    /**
     * restored=true は、前回の回答での再表示（診断を完了した数としては数えない）。
     * onlyIfUnlocked=true は、LINE解放済みの人にだけ前回の結果を自動表示する（未解放ならトップへ）。
     * 解放済みかどうかはHTMLに埋めず、サーバーの応答で判定する（ページキャッシュで他の人の状態が混ざらないように）。
     */
    function runDiagnosis(restored, onlyIfUnlocked) {
        clear();
        var box = el('div', 'kdg-card');
        box.appendChild(el('p', 'kdg-loading', 'AI指数のデータを確認しています…'));
        root.appendChild(box);

        var query = ['s', 'p', 'm', 'c'].map(function (k) {
            return k + '=' + encodeURIComponent(answers[k]);
        }).join('&');

        fetch(cfg.diagnosisUrl + (cfg.diagnosisUrl.indexOf('?') === -1 ? '?' : '&') + query, { credentials: 'same-origin' })
            .then(function (res) {
                if (!res.ok) {
                    throw new Error('status ' + res.status);
                }
                return res.json();
            })
            .then(function (data) {
                if (onlyIfUnlocked && !data.unlocked) {
                    showIntro();
                    return;
                }
                if (!restored) {
                    track('complete');
                }
                save(answers);
                if (data.status === 'preparing') {
                    showMessage('診断データを準備中です', '本日のAI指数のデータがまだそろっていません。しばらくしてからもう一度お試しください。', false);
                } else if (data.status === 'none') {
                    showMessage('いま、条件に合うレースが見つかりませんでした',
                        'これから発走するレースの中に、あなたの条件に合う基準以上のレースがありませんでした。条件を変えると見つかることがあります。', true);
                } else {
                    showResult(data);
                }
            })
            .catch(function () {
                showMessage('診断を取得できませんでした', '通信に失敗しました。時間をおいてもう一度お試しください。', true);
            });
    }

    function showResult(data) {
        clear();
        var wrap = el('div', 'kdg-result');

        var head = el('div', 'kdg-card kdg-result-head');
        head.appendChild(el('p', 'kdg-catch', 'あなたの診断タイプ'));
        head.appendChild(el('h2', 'kdg-type', '「' + data.type + '」'));
        if (data.relaxed) {
            head.appendChild(el('p', 'kdg-note', '人気の条件に合うレースがなかったため、人気の条件を外してお選びしています。'));
        }
        wrap.appendChild(head);

        var details = [];
        data.items.forEach(function (item, index) {
            var card = el('div', 'kdg-card kdg-race' + (index === 0 ? ' kdg-race-main' : ''));
            card.appendChild(el('p', 'kdg-catch', index === 0 ? 'あなたの今日の狙い目レース' : '他の注目レース'));

            var title = el('h3', 'kdg-race-title');
            title.appendChild(document.createTextNode(item.venue + ' ' + item.raceNumber + 'R'));
            card.appendChild(title);

            var meta = el('p', 'kdg-race-meta');
            var rank = el('span', 'kdg-rank kdg-rank-' + item.rank, '注目度 ' + item.rank);
            meta.appendChild(rank);
            meta.appendChild(document.createTextNode(' 発走 ' + item.startTime + (item.raceName ? '　' + item.raceName : '')));
            card.appendChild(meta);

            card.appendChild(el('p', 'kdg-reason-title', 'このレースを選んだ理由'));
            var ul = el('ul', 'kdg-reasons');
            item.reasons.forEach(function (reason) {
                ul.appendChild(el('li', null, reason));
            });
            card.appendChild(ul);

            var detail = el('div', 'kdg-detail');
            detail.hidden = true;
            card.appendChild(detail);
            details.push({ node: detail, item: item });

            card.appendChild(el('a', 'kdg-link', 'この開催場の無料予想（AI指数ダイジェスト）を見る →')).setAttribute('href', item.link);
            wrap.appendChild(card);
        });

        var cta = el('div', 'kdg-card kdg-cta');
        wrap.appendChild(cta);

        function renderDetails() {
            details.forEach(function (d) {
                while (d.node.firstChild) {
                    d.node.removeChild(d.node.firstChild);
                }
                d.node.appendChild(el('p', 'kdg-reason-title', '詳細な予想情報'));
                var table = el('table', 'kdg-table');
                var thead = el('thead');
                var hr = el('tr');
                ['馬番', '馬名', 'AI指数', '人気', '区分'].forEach(function (h) {
                    hr.appendChild(el('th', null, h));
                });
                thead.appendChild(hr);
                table.appendChild(thead);
                var tbody = el('tbody');
                (d.item.detail || []).forEach(function (row) {
                    var tr = el('tr');
                    tr.appendChild(el('td', null, row.umaban === null ? '-' : String(row.umaban)));
                    tr.appendChild(el('td', null, row.horseName || '-'));
                    tr.appendChild(el('td', null, row.aiIndex === null ? '-' : String(row.aiIndex)));
                    tr.appendChild(el('td', null, row.ninki === null ? '-' : row.ninki + '番人気'));
                    tr.appendChild(el('td', null, (row.roles || []).join('・')));
                    tbody.appendChild(tr);
                });
                table.appendChild(tbody);
                var scroll = el('div', 'kdg-table-scroll');
                scroll.appendChild(table);
                d.node.appendChild(scroll);
                d.node.hidden = false;
            });
        }

        function friendAddButton(label, className) {
            if (!cfg.addFriendUrl) {
                return null;
            }
            var a = el('a', className, label);
            a.href = cfg.addFriendUrl;
            a.target = '_blank';
            a.rel = 'noopener';
            a.addEventListener('click', function () {
                track('line_click');
            });
            return a;
        }

        if (data.unlocked) {
            track('detail_view');
            renderDetails();
            cta.appendChild(el('p', 'kdg-cta-title', '詳細をご覧いただけます'));
            var more = friendAddButton('LINE友だち追加がまだの方はこちら（無料）', 'kdg-btn kdg-btn-line');
            if (more) {
                cta.appendChild(more);
            }
        } else {
            cta.appendChild(el('p', 'kdg-cta-title', 'あなた向けの注目馬・AI指数を無料公開中！'));
            cta.appendChild(el('p', 'kdg-lead', '本命馬・注目穴馬・危険な人気馬・AI指数などの詳細は、LINE登録後にご覧いただけます。'));
            var line = friendAddButton('LINE登録で診断結果の詳細をチェック', 'kdg-btn kdg-btn-line');
            var open = button('本命馬・穴馬・AI指数などの詳細を見る', 'kdg-btn kdg-btn-primary', function () {
                track('detail_click');
                open.hidden = true;
                panel.hidden = false;
            });
            cta.appendChild(open);

            var panel = el('div', 'kdg-line-panel');
            panel.hidden = true;
            panel.appendChild(el('p', 'kdg-lead', 'LINE公式アカウントを友だち追加すると、診断結果の詳細（本命馬・穴馬・AI指数など）をご覧いただけます。'));
            if (line) {
                panel.appendChild(line);
            } else {
                panel.appendChild(el('p', 'kdg-note', 'LINEのご案内は準備中です。'));
            }
            panel.appendChild(el('p', 'kdg-note', '友だち追加後、LINEに届く専用リンクから、この診断の詳細が開きます。すでに友だち追加済みの方は、LINEのトークにある専用リンクからお進みください。'));
            cta.appendChild(panel);
        }

        var gen = el('a', 'kdg-btn kdg-btn-secondary', 'スマートAI予想ジェネレーターでレースを分析する →');
        gen.href = cfg.generatorUrl;
        gen.target = '_blank';
        gen.rel = 'noopener';
        cta.appendChild(gen);

        wrap.appendChild(button('もう一度診断する', 'kdg-btn kdg-btn-link', showIntro));
        wrap.appendChild(disclaimer());
        root.appendChild(wrap);
        scrollTop();
    }

    /* ---------------------------------------------------------------- 前回の結果（LINE登録後に戻ってきたとき用） */

    function save(a) {
        try {
            window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ a: a, t: Date.now() }));
        } catch (e) { /* 保存できなくても診断は続けられる */ }
    }

    function loadSaved() {
        try {
            var raw = window.localStorage.getItem(STORAGE_KEY);
            if (!raw) {
                return null;
            }
            var saved = JSON.parse(raw);
            if (!saved || !saved.a || Date.now() - saved.t > 24 * 60 * 60 * 1000) {
                return null; // 古い結果は使わない（その日のレースが変わるため）
            }
            var ok = QUESTIONS.every(function (q) {
                return q.options.some(function (o) { return o[0] === saved.a[q.key]; });
            });
            return ok ? saved.a : null;
        } catch (e) {
            return null;
        }
    }

    /* ---------------------------------------------------------------- 開始 */

    track('view');
    var savedAtStart = loadSaved();
    if (savedAtStart) {
        // LINE登録後に専用リンクから戻ってきた人は、診断をやり直さず結果の詳細へ。
        answers = savedAtStart;
        runDiagnosis(true, true);
    } else {
        showIntro();
    }
})();
