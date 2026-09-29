<?php
/**
 * Plugin Name: Keiba AI Digest
 * Description: KeibaDataCollector（JRAVAN+競馬最強の法則WEB 全自動AI競馬データシステム）から送られる
 *              AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬を、開催場・日単位のカスタム投稿タイプ
 *              「keiba_digest」として受け取り、表示する。既存の Keiba Race Sync（race投稿）とは別。
 * Version: 0.1.0
 */

if (!defined('ABSPATH')) {
    exit;
}

define('KEIBA_AI_DIGEST_VERSION', '0.1.0');
define('KEIBA_AI_DIGEST_JSON_META_KEYS', array(
    'ai_index_top5',
    'trend_morning',
    'trend_live',
    'trend_final',
    'picks',
));

/**
 * カスタム投稿タイプ「keiba_digest」を登録。1開催場・1日につき1投稿
 * （digest_keyで一意。WordPressClient.UpsertDigestAsyncがこれを検索キーに使う）。
 */
add_action('init', function () {
    register_post_type('keiba_digest', array(
        'label' => 'AI指数ダイジェスト',
        'labels' => array(
            'name' => 'AI指数ダイジェスト',
            'singular_name' => 'AI指数ダイジェスト',
        ),
        'public' => true,
        'show_in_rest' => true,
        'rest_base' => 'keiba_digest',
        'supports' => array('title', 'custom-fields'),
        'has_archive' => true,
        'rewrite' => array('slug' => 'ai-digest'),
        'menu_icon' => 'dashicons-chart-line',
    ));
});

/**
 * KeibaDataCollectorが送るメタキーをREST経由で読み書き可能にする。
 * ai_index_top5 / trend_* / picks はcamelCaseキーのJSON文字列として保存する取り決め
 * （WordPressClient.cs側もこの形式で送信する。keiba-race-syncのrace_card等と同じ方針）。
 */
add_action('init', function () {
    foreach (KEIBA_AI_DIGEST_JSON_META_KEYS as $meta_key) {
        register_post_meta('keiba_digest', $meta_key, array(
            'type' => 'string',
            'single' => true,
            'show_in_rest' => true,
            'sanitize_callback' => 'keiba_ai_digest_sanitize_json_meta',
            'auth_callback' => function () {
                return current_user_can('edit_posts');
            },
        ));
    }

    register_post_meta('keiba_digest', 'digest_key', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    register_post_meta('keiba_digest', 'race_date', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    register_post_meta('keiba_digest', 'track_code', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    // LicenseGateの判定結果をそのまま持たせておく。万一データが残っていても
    // 表示側でも二重に隠せるようにする（horse-race-custom-builderのhrc_is_race_visibleと同じ考え方）。
    register_post_meta('keiba_digest', 'license_visible', array(
        'type' => 'boolean',
        'single' => true,
        'show_in_rest' => true,
        'default' => false,
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    register_post_meta('keiba_digest', 'updated_at', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));
});

/**
 * WordPressClient.cs の FindDigestPostIdAsync が使う
 * GET /wp-json/wp/v2/keiba_digest?meta_key=digest_key&meta_value=xxxx を有効にする。
 * keiba-race-syncのrest_race_queryフィルタと同じ考え方（総当たり探索を防ぐため digest_key のみ許可）。
 */
add_filter('rest_keiba_digest_query', function ($args, $request) {
    if ($request->get_param('meta_key') === 'digest_key') {
        $meta_value = $request->get_param('meta_value');
        if ($meta_value !== null && $meta_value !== '') {
            $args['meta_query'] = array(
                array(
                    'key' => 'digest_key',
                    'value' => sanitize_text_field($meta_value),
                ),
            );
        }
    }
    return $args;
}, 10, 2);

function keiba_ai_digest_sanitize_json_meta($value)
{
    if (is_array($value) || is_object($value)) {
        return wp_json_encode($value);
    }
    $value = (string) $value;
    json_decode($value);
    return json_last_error() === JSON_ERROR_NONE ? $value : '[]';
}

/**
 * 仕様書§13「管理画面に自動公開ON/OFFと手動再実行を用意」のうち、自動公開ON/OFF部分。
 * WordPressClient.IsAutoPublishEnabledAsyncがこのGETエンドポイントをポーリングする。
 * 「手動再実行」自体は、収集アプリ側のrun-*.batを運用者がVPS上で直接再実行する運用のまま
 * にしている（このプラグインからVPS上のプロセスを起動する経路は意図的に作らない。
 * Webから任意のバッチ実行を引き起こせる経路を増やすとその分だけ攻撃面が増えるため）。
 */
add_action('rest_api_init', function () {
    register_rest_route('keiba-ai/v1', '/settings', array(
        'methods' => 'GET',
        'permission_callback' => '__return_true',
        'callback' => function () {
            return array(
                'version' => KEIBA_AI_DIGEST_VERSION,
                'autoPublishEnabled' => (bool) get_option('keiba_ai_digest_auto_publish_enabled', false),
            );
        },
    ));

    register_rest_route('keiba-ai/v1', '/settings', array(
        'methods' => 'POST',
        'permission_callback' => function () {
            return current_user_can('manage_options');
        },
        'callback' => function (WP_REST_Request $request) {
            $enabled = (bool) $request->get_param('autoPublishEnabled');
            update_option('keiba_ai_digest_auto_publish_enabled', $enabled);
            return array('autoPublishEnabled' => $enabled);
        },
    ));
});

/**
 * 管理画面: 設定 > Keiba AI Digest。自動公開ON/OFFのチェックボックスのみ持つ。
 *
 * 既定値はOFF（フェイルクローズ）。仕様書§5・LicenseGateと同じ方針で、
 * 「許諾・準備が整うまでは公開しない」を初期状態にしている。プラグイン導入直後に
 * 意図せず自動公開が始まらないようにするため。
 */
add_action('admin_menu', function () {
    add_options_page(
        'Keiba AI Digest',
        'Keiba AI Digest',
        'manage_options',
        'keiba-ai-digest',
        'keiba_ai_digest_render_settings_page'
    );
});

function keiba_ai_digest_render_settings_page()
{
    if (!current_user_can('manage_options')) {
        return;
    }

    if (isset($_POST['keiba_ai_digest_nonce']) && wp_verify_nonce($_POST['keiba_ai_digest_nonce'], 'keiba_ai_digest_settings')) {
        update_option('keiba_ai_digest_auto_publish_enabled', isset($_POST['auto_publish_enabled']));
        echo '<div class="notice notice-success"><p>保存しました。</p></div>';
    }

    $enabled = (bool) get_option('keiba_ai_digest_auto_publish_enabled', false);
    ?>
    <div class="wrap">
        <h1>Keiba AI Digest 設定</h1>
        <p>
            AI指数TOP5・本日の傾向・狙い馬/穴馬/危険な人気馬のWordPress自動公開を制御します。
            OFFの間は、収集アプリ側でLicenseGate・Validatorの判定が通っていてもWordPressへは反映されません
            （収集アプリ側のスコア算出・コンテンツ生成・ローカルDBへの保存自体は止まりません）。
        </p>
        <form method="post">
            <?php wp_nonce_field('keiba_ai_digest_settings', 'keiba_ai_digest_nonce'); ?>
            <table class="form-table">
                <tr>
                    <th scope="row">自動公開</th>
                    <td>
                        <label>
                            <input type="checkbox" name="auto_publish_enabled" value="1" <?php checked($enabled); ?> />
                            AI指数・傾向・狙い目の自動公開を有効にする
                        </label>
                    </td>
                </tr>
            </table>
            <?php submit_button('保存'); ?>
        </form>
        <h2>手動再実行について</h2>
        <p>
            個別レースの再取得・再公開は、収集アプリ（VPS上のKeibaDataCollector）側で
            <code>run-score.bat</code> / <code>run-content.bat</code> を対象日を指定して再実行してください。
            このプラグインからVPS上の処理を起動する経路は用意していません。
        </p>
    </div>
    <?php
}
